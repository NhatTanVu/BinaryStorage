using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Caching;
using BinStorage.Extensions;
using System.Collections.Concurrent;
using System.Threading;
using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Linq;
using System.IO.Compression;

namespace BinStorage
{
    public class BinaryStorage : IBinaryStorage
    {
        private static object _lockObject = new object();

        private readonly StorageConfiguration storageConfiguration;
        private readonly string indexFilePath;
        private readonly string backupStorageFileName = "storage.bin.bak";
        private readonly string storageFilePath;
        private readonly string backupStorageFilePath;
        private readonly int cacheSize = 1024; // 1024MB

        private ConcurrentDictionary<string, BinaryIndex> indexTable;
        private MemoryCache storageCache;

        private ConcurrentDictionary<string, BinaryIndex> indexTableBuffer;
        private ConcurrentQueue<byte[]> storageBuffer;
        private long storageBufferLength = 0;
        private const int MAX_BUFFER_INDEX_SIZE = 1024 * 1024; // 1MB
        private const int MAX_BUFFER_STORAGE_SIZE = 1024 * 1024; // 1MB
        private const int MAX_BUFFER_ADD_SIZE = 16 * 1024; // 16KB

        public static string IndexFileName = "index.bin";
        public static string StorageFileName = "storage.bin";

        public BinaryStorage(StorageConfiguration configuration)
        {
            this.storageConfiguration = configuration;
            this.indexTable = new ConcurrentDictionary<string, BinaryIndex>();
            this.indexFilePath = Path.Combine(configuration.WorkingFolder, IndexFileName);
            this.storageFilePath = Path.Combine(configuration.WorkingFolder, StorageFileName);
            this.backupStorageFilePath = Path.Combine(configuration.WorkingFolder, this.backupStorageFileName);

            this.indexTableBuffer = new ConcurrentDictionary<string, BinaryIndex>();
            this.storageBuffer = new ConcurrentQueue<byte[]>();

            ConfigureStorageCache();
            if (File.Exists(this.indexFilePath) && File.Exists(this.storageFilePath))
                LoadIndex();
            else
            {
                SaveIndex();
                CreateStorage();
            }
        }

        #region Public Methods
        public void Add(string key, Stream data, StreamInfo parameters)
        {
            if (key == null)
                throw new ArgumentNullException("key is null");
            if (data == null)
                throw new ArgumentNullException("data is null");
            if (parameters == null)
                throw new ArgumentNullException("parameters is null");

            if (CheckContains(key))
                throw new ArgumentException("An element with the same key already exists or provided hash or length does not match the data");
            CheckMaxIndexFile();

            StreamInfo cloneParameters = (StreamInfo)parameters.Clone();
            long firstBufferLength = MAX_BUFFER_ADD_SIZE;
            if (this.storageConfiguration.CompressionThreshold > 0)
                firstBufferLength = this.storageConfiguration.CompressionThreshold + 1;
            List<byte> outputBytes = new List<byte>();
            List<long> compressedChunksLength = new List<long>();

            using (MD5 hasher = MD5.Create())
            {
                hasher.Initialize();
                byte[] buffer = new byte[firstBufferLength];
                int read = data.Read(buffer, 0, (int)firstBufferLength);
                if (read < firstBufferLength)
                    buffer = buffer.Take(read).ToArray();

                bool needCompressed = false;
                if (!parameters.IsCompressed && (read > this.storageConfiguration.CompressionThreshold))
                {
                    needCompressed = true;
                    outputBytes.AddRange(CompressBytes(buffer, 0, read));
                    cloneParameters.IsManuallyCompressed = true;
                    compressedChunksLength.Add(outputBytes.Count);
                }
                else
                    outputBytes.AddRange(buffer);
                hasher.TransformBlock(buffer, 0, read, null, 0);

                buffer = new byte[MAX_BUFFER_ADD_SIZE];
                while ((read = data.Read(buffer, 0, MAX_BUFFER_ADD_SIZE)) > 0)
                {
                    if (needCompressed)
                    {
                        byte[] compressedChunk = CompressBytes(buffer, 0, read);
                        outputBytes.AddRange(compressedChunk);
                        compressedChunksLength.Add(compressedChunk.Length);
                    }
                    else
                        outputBytes.AddRange(buffer);
                    hasher.TransformBlock(buffer, 0, read, null, 0);
                }
                // https://stackoverflow.com/questions/3621283/compute-a-hash-from-a-stream-of-unknown-length-in-c-sharp
                hasher.TransformFinalBlock(new byte[0], 0, 0);

                byte[] hash = hasher.Hash;
                if (parameters.Hash != null)
                    CheckHash(hash, parameters);
                else
                    cloneParameters.Hash = hash;
            }

            lock (_lockObject)
            {
                byte[] dataBytes = outputBytes.ToArray();
                BinaryIndex index = FindBinaryIndexByHash(cloneParameters);
                bool isNew = false;
                if (index == null)
                {
                    CheckMaxStorageFile(dataBytes);
                    index = CreateBinaryIndex(dataBytes, compressedChunksLength.ToArray(), cloneParameters);
                    isNew = true;
                }
                if (!this.indexTableBuffer.TryAdd(key, index))
                    throw new ArgumentException("An element with the same key already exists");
                else if (isNew)
                {
                    storageBufferLength += outputBytes.Count;
                    this.storageBuffer.Enqueue(dataBytes);
                }

                FlushBuffer();
            }
        }

        public Stream Get(string key)
        {
            FlushBuffer(false);

            if (key == null)
                throw new ArgumentNullException("key is null");

            if (!CheckContains(key))
                throw new KeyNotFoundException("key does not exist");

            Stream result = GetFromCache(key);
            if (result != null)
            {
                BinaryIndex index;
                if (indexTable.TryGetValue(key, out index))
                {
                    byte[] storageData = ((MemoryStream)result).ToArray();
                    result = Decompress(storageData, index.Reference.CompressedChunksLength, index.Information);
                }
                else
                    throw new KeyNotFoundException("key does not exist");
            }
            else
            {
                BinaryIndex index;
                if (indexTable.TryGetValue(key, out index))
                {
                    byte[] storageData = GetBytesFromStorageFile(index);
                    if (storageData != null)
                    {
                        result = Decompress(storageData, index.Reference.CompressedChunksLength, index.Information);
                        AddToCache(key, new MemoryStream(storageData));
                    }
                    else
                        throw new KeyNotFoundException("key does not exist");
                }
                else
                    throw new KeyNotFoundException("key does not exist");
            }

            return result;
        }

        public bool Contains(string key)
        {
            FlushBuffer(false);
            return CheckContains(key);
        }

        public void Dispose()
        {
            FlushBuffer(false);
            ClearCache();
        }
        #endregion

        #region Private Methods
        private void ConfigureStorageCache()
        {
            NameValueCollection cacheSettings = new NameValueCollection(3);
            cacheSettings.Add("CacheMemoryLimitMegabytes", Convert.ToString(this.cacheSize));
            cacheSettings.Add("physicalMemoryLimitPercentage", Convert.ToString(50));  //set % here
            cacheSettings.Add("pollingInterval", Convert.ToString("00:00:10"));
            this.storageCache = new MemoryCache("storageCache", cacheSettings);
        }

        private Stream GetIndexStream(ConcurrentDictionary<string, BinaryIndex> indexTable)
        {
            return indexTable.Serialize();
        }

        private long GetIndexStreamLength(ConcurrentDictionary<string, BinaryIndex> indexTable)
        {
            long indexStreamSize = 0;
            using (Stream stream = GetIndexStream(indexTable))
            {
                indexStreamSize = stream.Length;
            }
            return indexStreamSize;
        }

        private void LoadIndex()
        {
            using (FileStream fs = File.Open(this.indexFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                this.indexTable = (ConcurrentDictionary<string, BinaryIndex>)fs.Decompress().Deserialize();
            }
        }

        private bool CheckContains(string key)
        {
            return ExistedInCache(key) || ExistedInIndex(key);
        }

        private void CheckMaxIndexFile()
        {
            long indexFileSize = GetIndexStreamLength(indexTable) + GetIndexStreamLength(indexTableBuffer);
            if ((this.storageConfiguration.MaxIndexFile > 0) && (indexFileSize > this.storageConfiguration.MaxIndexFile))
                throw new Exception("MaxIndexFile was reached");
        }

        private void FlushBuffer(bool requiredCheck = true)
        {
            lock (_lockObject)
            {
                FlushIndexBuffer(requiredCheck);
                FlushStorageBuffer(requiredCheck);
            }
        }

        private void FlushIndexBuffer(bool requiredCheck)
        {
            if (!this.indexTableBuffer.IsEmpty && (!requiredCheck || GetIndexStreamLength(this.indexTableBuffer) >= MAX_BUFFER_INDEX_SIZE))
            {
                try
                {
                    this.indexTableBuffer.ToList().ForEach(x => this.indexTable.TryAdd(x.Key, x.Value));
                    SaveIndex();
                    FlushStorageBuffer(false);
                    this.indexTableBuffer.Clear();
                }
                catch
                {
                    this.indexTableBuffer.ToList().ForEach(x => this.indexTable.TryRemove(x.Key, out BinaryIndex value));
                    SaveIndex();
                }
            }
        }

        private void FlushStorageBuffer(bool requiredCheck)
        {
            if (this.storageBufferLength > 0 && (!requiredCheck || this.storageBufferLength >= MAX_BUFFER_STORAGE_SIZE))
            {
                try
                {
                    BackupStorage();
                    byte[] result = new byte[0];
                    byte[] temp;
                    while (this.storageBuffer.TryDequeue(out temp))
                    {
                        this.storageBufferLength -= temp.Length;
                        result = result.Concat(temp).ToArray();
                    }
                    AppendStorage(result);
                    FlushIndexBuffer(false);
                }
                catch
                {
                    RestoreBackupStorage();
                }
                finally
                {
                    RemoveBackupStorage();
                }
            }
        }

        private void SaveIndex()
        {
            using (FileStream fileStream = File.Open(this.indexFilePath, FileMode.Create))
            {
                using (Stream stream = GetIndexStream(this.indexTable))
                {
                    stream.Compress().CopyTo(fileStream);
                }
            }
        }

        private BinaryIndex FindBinaryIndexByHash(StreamInfo parameters)
        {
            KeyValuePair<string, BinaryIndex> result;
            result = this.indexTable.AsParallel().FirstOrDefault(index => index.Value.Information.Hash.SequenceEqual(parameters.Hash));
            if (result.Equals(default(KeyValuePair<string, BinaryIndex>)))
                result = this.indexTableBuffer.AsParallel().FirstOrDefault(index => index.Value.Information.Hash.SequenceEqual(parameters.Hash));
            return result.Equals(default(KeyValuePair<string, BinaryIndex>)) ? null : result.Value;
        }

        private long GetStorageFileLength()
        {
            return new FileInfo(this.storageFilePath).Length;
        }

        private void CreateStorage()
        {
            File.Create(this.storageFilePath).Close();
        }

        private void AppendStorage(byte[] data)
        {
            using (FileStream fileStream = File.Open(this.storageFilePath, FileMode.Append))
            {
                fileStream.Write(data, 0, data.Length);
            }
        }

        private void BackupStorage()
        {
            File.Copy(this.storageFilePath, this.backupStorageFilePath, true);
        }

        private void RestoreBackupStorage()
        {
            File.Copy(this.backupStorageFilePath, this.storageFilePath, true);
        }

        private void RemoveBackupStorage()
        {
            File.Delete(this.backupStorageFilePath);
        }

        private Stream GetFromCache(string key)
        {
            Stream result = (Stream)this.storageCache.Get(key);
            return result;
        }

        private void AddToCache(string key, Stream result)
        {
            this.storageCache.Set(key, result, DateTimeOffset.MaxValue);
        }

        private void ClearCache()
        {
            this.storageCache.Dispose();
        }

        private bool ExistedInCache(string key)
        {
            return this.storageCache.Contains(key);
        }

        private bool ExistedInIndex(string key)
        {
            return this.indexTable.ContainsKey(key) || this.indexTableBuffer.ContainsKey(key);
        }

        private byte[] CompressBytes(byte[] data, int offset, int length)
        {
            // https://stackoverflow.com/questions/39191950/how-to-compress-a-byte-array-without-stream-or-system-io
            MemoryStream output = new MemoryStream();
            using (DeflateStream dstream = new DeflateStream(output, CompressionLevel.Optimal))
            {
                dstream.Write(data, offset, length);
            }
            return output.ToArray();
        }

        private byte[] DecompressBytes(byte[] data)
        {
            MemoryStream input = new MemoryStream(data);
            MemoryStream output = new MemoryStream();
            using (DeflateStream dstream = new DeflateStream(input, CompressionMode.Decompress))
            {
                dstream.CopyTo(output);
            }
            return output.ToArray();
        }

        private void CheckHash(byte[] hash, StreamInfo parameters)
        {
            if (!hash.SequenceEqual(parameters.Hash))
            {
                throw new ArgumentException("Provided hash does not match the data");
            }
        }

        private void CheckMaxStorageFile(byte[] data)
        {
            long storageFileSize = GetStorageFileLength() + this.storageBufferLength;
            if ((this.storageConfiguration.MaxStorageFile > 0) && ((storageFileSize + data.Length) > this.storageConfiguration.MaxStorageFile))
                throw new Exception("MaxStorageFile was reached");
        }

        private BinaryIndex CreateBinaryIndex(byte[] data, long[] compressedChunksLength, StreamInfo parameters)
        {
            long offset = GetStorageFileLength() + this.storageBufferLength;
            BinaryIndex result = new BinaryIndex()
            {
                Reference = new BinaryReference()
                {
                    Offset = offset,
                    Length = data.Length,
                    CompressedChunksLength = compressedChunksLength
                },
                Information = parameters
            };

            return result;
        }

        private byte[] GetBytesFromStorageFile(BinaryIndex index)
        {
            byte[] data = null;
            using (FileStream fs = File.Open(this.storageFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(index.Reference.Offset, SeekOrigin.Begin);
                data = new byte[index.Reference.Length];
                fs.Read(data, 0, data.Length);
            }

            return data;
        }

        private Stream Decompress(byte[] data, long[] compressedChunksLength, StreamInfo parameters)
        {
            // https://stackoverflow.com/questions/34776031/how-to-create-a-temp-file-that-is-automatically-deleted-after-the-program-termin/34776237
            string tempFilePath = Path.GetTempFileName();
            FileStream result = new FileStream(tempFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            if (parameters.IsManuallyCompressed)
            {
                int offset = 0;
                List<byte> outputData = new List<byte>();
                foreach (long chunklength in compressedChunksLength)
                {
                    byte[] truncArray = new byte[chunklength];
                    Array.Copy(data, offset, truncArray, 0, chunklength);
                    outputData.AddRange(DecompressBytes(truncArray));
                    offset += (int)chunklength;
                }
                byte[] resultBytes = outputData.ToArray();
                result.Write(resultBytes, 0, resultBytes.Length);
                result.Seek(0, SeekOrigin.Begin);
                return result;
            }
            else
            {
                result.Write(data, 0, data.Length);
                result.Seek(0, SeekOrigin.Begin);
                return new MemoryStream(data);
            }
        }
        #endregion
    }
}