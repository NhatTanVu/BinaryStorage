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

namespace BinStorage
{
    public class BinaryStorage : IBinaryStorage
    {
        private static readonly ReaderWriterLockSlim rwLock = new ReaderWriterLockSlim();

        private readonly StorageConfiguration storageConfiguration;
        private readonly string indexFileName = "index.bin";
        private readonly string indexFilePath;
        private readonly string storageFileName = "storage.bin";
        private readonly string backupStorageFileName = "storage.bin.bak";
        private readonly string storageFilePath;
        private readonly string backupStorageFilePath;
        private readonly long cacheSize = 1024; // 1024 MB

        private ConcurrentDictionary<string, BinaryIndex> indexTable;
        private MemoryCache storageCache;

        public BinaryStorage(StorageConfiguration configuration)
        {
            this.storageConfiguration = configuration;
            this.indexTable = new ConcurrentDictionary<string, BinaryIndex>();
            this.indexFilePath = Path.Combine(configuration.WorkingFolder, this.indexFileName);
            this.storageFilePath = Path.Combine(configuration.WorkingFolder, this.storageFileName);
            this.backupStorageFilePath = Path.Combine(configuration.WorkingFolder, this.backupStorageFileName);

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
            CheckDataStream(data, parameters);

            using (rwLock.Write())
            {
                if (Contains(key))
                    throw new ArgumentException("An element with the same key already exists or provided hash or length does not match the data");
                CheckMaxIndexFile();

                StreamInfo cloneParameters = (StreamInfo)parameters.Clone();
                using (Stream compressedData = CompressIfNot(data, ref cloneParameters))
                {
                    CheckMaxStorageFile(compressedData);

                    try
                    {
                        BinaryIndex newIndex = CreateBinaryIndex(compressedData, cloneParameters);
                        if (!this.indexTable.TryAdd(key, newIndex))
                            throw new ArgumentException("An element with the same key already exists");
                        else
                        {
                            Backup();
                            SaveIndex();
                            SaveStorage(compressedData);
                        }
                    }
                    catch
                    {
                        RestoreBackup();
                        BinaryIndex removedIndex;
                        while (!this.indexTable.TryRemove(key, out removedIndex))
                            Thread.Sleep(500);
                        SaveIndex();
                    }
                    finally
                    {
                        RemoveBackup();
                    }
                }
            }
        }

        public Stream Get(string key)
        {
            if (key == null)
                throw new ArgumentNullException("key is null");

            using (rwLock.Read())
            {
                if (!Contains(key))
                    throw new KeyNotFoundException("key does not exist");

                Stream result = GetFromCache(key);
                if (result != null)
                {
                    BinaryIndex index;
                    if (indexTable.TryGetValue(key, out index))
                    {
                        result = DecompressIfNeeded(result, index.Information);
                    }
                    else
                        throw new KeyNotFoundException("key does not exist");
                }
                else
                {
                    BinaryIndex index;
                    if (indexTable.TryGetValue(key, out index))
                    {
                        Stream storageStream = GetFromStorageFile(index);
                        if (storageStream != null)
                        {
                            result = DecompressIfNeeded(storageStream, index.Information);
                            AddToCache(key, storageStream);
                        }
                        else
                            throw new KeyNotFoundException("key does not exist");
                    }
                    else
                        throw new KeyNotFoundException("key does not exist");
                }

                return result;
            }
        }

        public bool Contains(string key)
        {
            if (rwLock.IsWriteLockHeld || rwLock.IsReadLockHeld)
            {
                return (ExistedInCache(key) || ExistedInIndex(key)) && !IsFileChanged(this.indexFilePath) && !IsFileChanged(this.storageFilePath);
            }
            else
            {
                using (rwLock.Read())
                {
                    return (ExistedInCache(key) || ExistedInIndex(key)) && !IsFileChanged(this.indexFilePath) && !IsFileChanged(this.storageFilePath);
                }
            }
        }

        public void Dispose()
        {
            ClearCache();
            RemoveBackup();
            ObjectSerializer.ClearTempFiles();
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

        private Stream GetIndexStream()
        {
            return this.indexTable.Serialize();
        }

        private long GetIndexStreamLength()
        {
            long indexStreamSize = 0;
            using (Stream stream = GetIndexStream())
            {
                indexStreamSize = stream.Length;
            }
            return indexStreamSize;
        }

        private void LoadIndex()
        {
            using (FileStream fs = File.Open(this.indexFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                this.indexTable = (ConcurrentDictionary<string, BinaryIndex>)fs.Deserialize();
            }
        }

        private void CheckMaxIndexFile()
        {
            long indexFileSize = GetIndexStreamLength();
            if ((this.storageConfiguration.MaxIndexFile > 0) && (indexFileSize > this.storageConfiguration.MaxIndexFile))
                throw new Exception("MaxIndexFile was reached");
        }

        private void SaveIndex()
        {
            using (FileStream fileStream = File.Open(this.indexFilePath, FileMode.Create))
            {
                using (Stream stream = GetIndexStream())
                {
                    stream.CopyTo(fileStream);
                }
            }
        }

        private BinaryIndex CreateBinaryIndex(Stream data, StreamInfo parameters)
        {
            long offset = GetStorageFileLength();
            BinaryIndex result = new BinaryIndex()
            {
                Reference = new BinaryReference()
                {
                    Offset = offset,
                    Length = data.Length
                },
                Information = parameters
            };

            return result;
        }

        private long GetStorageFileLength()
        {
            return new FileInfo(this.storageFilePath).Length;
        }

        private void CheckDataStream(Stream data, StreamInfo parameters)
        {
            if (parameters.Length.HasValue && data.Length != parameters.Length)
                throw new ArgumentException("Provided length does not match the data");
            if (parameters.Hash != null)
            {
                byte[] hash1;
                using (MD5 md5 = MD5.Create())
                {
                    hash1 = md5.ComputeHash(data);
                    if (!hash1.SequenceEqual(parameters.Hash))
                    {
                        throw new ArgumentException("Provided hash does not match the data");
                    }
                }
            }
        }

        private void CheckMaxStorageFile(Stream data)
        {
            long storageFileSize = GetStorageFileLength();
            if ((this.storageConfiguration.MaxStorageFile > 0) && ((storageFileSize + data.Length) > this.storageConfiguration.MaxStorageFile))
                throw new Exception("MaxStorageFile was reached");
        }

        private void CreateStorage()
        {
            File.Create(this.storageFilePath).Close();
        }

        private void SaveStorage(Stream data)
        {
            using (FileStream fileStream = File.Open(this.storageFilePath, FileMode.Append))
            {
                data.CopyTo(fileStream);
            }
        }

        private void Backup()
        {
            File.Copy(this.storageFilePath, this.backupStorageFilePath, true);
        }

        private void RestoreBackup()
        {
            File.Copy(this.backupStorageFilePath, this.storageFilePath, true);
        }

        private void RemoveBackup()
        {
            File.Delete(this.backupStorageFilePath);
        }

        private Stream GetFromCache(string key)
        {
            Stream result = (Stream)this.storageCache.Get(key);
            return result;
        }

        private Stream GetFromStorageFile(BinaryIndex index)
        {
            MemoryStream result = null;
            using (FileStream fs = File.Open(this.storageFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(index.Reference.Offset, SeekOrigin.Begin);
                byte[] data = new byte[index.Reference.Length];
                fs.Read(data, 0, data.Length);
                result = new MemoryStream(data);
            }

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
            return this.indexTable.ContainsKey(key);
        }

        private Stream CompressIfNot(Stream result, ref StreamInfo parameters)
        {
            if (!parameters.IsCompressed && (result.Length > this.storageConfiguration.CompressionThreshold))
            {
                parameters.IsCompressed = true;
                return result.Compress();
            }
            else
                return result;
        }

        private Stream DecompressIfNeeded(Stream result, StreamInfo parameters)
        {
            if (parameters.IsCompressed)
                return result.Decompress();
            else
            {
                Stream clonedStream = new MemoryStream();
                result.CopyTo(clonedStream);
                result.Seek(0, SeekOrigin.Begin);
                clonedStream.Seek(0, SeekOrigin.Begin);
                return clonedStream;
            }
        }

        private bool IsFileChanged(string filePath)
        {
            // TODO: Check if the file content still matches with the corresponding data in memory
            return false;
        }
        #endregion
    }
}
