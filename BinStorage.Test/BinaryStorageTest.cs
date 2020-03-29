using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BinStorage.Test
{
    [TestClass]
    public class BinaryStorageTest
    {
        private readonly string inputBasePath = ".\\_input";
        private readonly string assertBasePath = ".\\_assert";
        private readonly string outputBasePath = ".\\_output";
        private readonly int degreeOfParallelism = 12;

        [TestMethod]
        public void ShouldAdd()
        {
            string shouldAddFolder = "ShouldAdd";
            AddAndCheck(shouldAddFolder);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException),
            "An element with the same key already exists or provided hash or length does not match the data")]
        public void ShouldNotAddDuplicatedKey()
        {
            // Arrange
            string inputFolder = "ShouldNotAddDuplicateKey";
            string inputFolderPath = Path.Combine(this.inputBasePath, inputFolder);
            string assertFileName = "16ddb0c9-9a88-47f2-a582-f7f09322506b.bin";
            string assertFilePath = Path.Combine(this.assertBasePath, inputFolder, assertFileName);
            // Act
            using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = inputFolderPath }))
            {
                AddFileStream(storage, assertFilePath, assertFileName);
            }
        }

        [TestMethod]
        public void ShouldStoreOnce()
        {
            string shouldStoreOnceFolder = "ShouldStoreOnce";
            AddAndCheck(shouldStoreOnceFolder);
        }

        [TestMethod]
        public void ShouldGet()
        {
            // Arrange
            string inputFolder = "ShouldGet";
            string inputFolderPath = Path.Combine(this.inputBasePath, inputFolder);
            string assertFolderPath = Path.Combine(this.assertBasePath, inputFolder);
            // Act
            using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = inputFolderPath }))
            {
                Directory.EnumerateFiles(assertFolderPath, "*", SearchOption.AllDirectories)
                    .AsParallel().WithDegreeOfParallelism(degreeOfParallelism).ForAll(s =>
                    {
                        string fileName = Path.GetFileName(s);
                        using (var resultStream = storage.Get(fileName))
                        {
                            using (var sourceStream = new FileStream(s, FileMode.Open, FileAccess.Read))
                            {
                                if (!AreStreamsEqual(s, sourceStream, resultStream))
                                {
                                    // Assert
                                    Assert.IsTrue(false);
                                }
                            }
                        }
                    });
            }
            // Assert
            Assert.IsTrue(true);
        }

        [TestMethod]
        public void ShouldGetAndNotDecompressIfAddedCompressed()
        {
            // Arrange
            string inputFolder = "ShouldGetAndNotDecompressIfAddedCompressed";
            string inputFolderPath = Path.Combine(this.inputBasePath, inputFolder);
            string assertFileName = "random_bytes.rar";
            string assertFilePath = Path.Combine(this.assertBasePath, inputFolder, assertFileName);
            // Act
            using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = inputFolderPath }))
            {
                using (var resultStream = storage.Get(assertFileName))
                {
                    using (var sourceStream = new FileStream(assertFilePath, FileMode.Open, FileAccess.Read))
                    {
                        // Assert
                        Assert.IsTrue(AreStreamsEqual(assertFileName, sourceStream, resultStream));
                    }
                }
            }
        }

        [TestMethod]
        public void ShouldContain()
        {
            // Arrange
            string shouldContainFolder = "ShouldGet";
            string inputFolderPath = Path.Combine(this.inputBasePath, shouldContainFolder);
            string assertFolderPath = Path.Combine(this.assertBasePath, shouldContainFolder);
            // Act
            using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = inputFolderPath }))
            {
                Directory.EnumerateFiles(assertFolderPath, "*", SearchOption.AllDirectories)
                    .AsParallel().WithDegreeOfParallelism(degreeOfParallelism).ForAll(s =>
                    {
                        string fileName = Path.GetFileName(s);
                        if (!storage.Contains(fileName))
                            Assert.Fail();
                    });
            }
            // Assert
            Assert.IsTrue(true);
        }

        [TestMethod]
        public void ShouldNotCompressIfAlreadyCompressed()
        {
            // Arrange
            string shouldNotCompress = "ShouldNotCompressIfAlreadyCompressed";
            string inputFolderPath = Path.Combine(this.inputBasePath, shouldNotCompress);
            string outputFolderPath = Path.Combine(this.outputBasePath, shouldNotCompress);
            string outputStoragePath = Path.Combine(outputFolderPath, BinaryStorage.StorageFileName);
            long assertLength = 0;

            if (!Directory.Exists(outputFolderPath))
                Directory.CreateDirectory(outputFolderPath);
            else
                EmptyFolder(outputFolderPath);
            // Act
            using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = outputFolderPath }))
            {
                Directory.GetFiles(inputFolderPath, "*", SearchOption.AllDirectories).ToList()
                    .ForEach(s =>
                    {
                        assertLength += new FileInfo(s).Length;
                        string fileName = Path.GetFileName(s);
                        StreamInfo info = new StreamInfo()
                        {
                            IsCompressed = true
                        };
                        AddFileStream(storage, s, fileName, info);
                    });
            }
            // Assert
            long actualLength = new FileInfo(outputStoragePath).Length;
            Assert.AreEqual(assertLength, actualLength);
        }

        [TestMethod]
        public void ShouldNotCompressIfUnderThreshold()
        {
            // Arrange
            string shouldNotCompress = "ShouldNotCompressIfUnderThreshold";
            string inputFolderPath = Path.Combine(this.inputBasePath, shouldNotCompress);
            string outputFolderPath = Path.Combine(this.outputBasePath, shouldNotCompress);
            string outputStoragePath = Path.Combine(outputFolderPath, BinaryStorage.StorageFileName);
            long compressionThreshold = 1024 * 12; // 12 KB
            long assertLength = 0;

            if (!Directory.Exists(outputFolderPath))
                Directory.CreateDirectory(outputFolderPath);
            else
                EmptyFolder(outputFolderPath);
            // Act
            using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = outputFolderPath, CompressionThreshold = compressionThreshold }))
            {
                Directory.GetFiles(inputFolderPath, "*", SearchOption.AllDirectories).ToList()
                    .ForEach(s =>
                    {
                        assertLength += new FileInfo(s).Length;
                        string fileName = Path.GetFileName(s);
                        AddFileStream(storage, s, fileName);
                    });
            }
            // Assert
            long actualLength = new FileInfo(outputStoragePath).Length;
            Assert.AreEqual(assertLength, actualLength);
        }

        [TestMethod]
        public void ShouldAddNetworkStream()
        {
            string shouldAddFolder = "ShouldAdd";
            AddAndCheck(shouldAddFolder, isNetwork: true);
        }

        #region Private Methods
        private void AddAndCheck(string testCaseFolder, bool isNetwork = false)
        {
            // Arrange
            string inputFolderPath = Path.Combine(this.inputBasePath, testCaseFolder);
            string assertIndexPath = Path.Combine(this.assertBasePath, testCaseFolder, BinaryStorage.IndexFileName);
            string assertStoragePath = Path.Combine(this.assertBasePath, testCaseFolder, BinaryStorage.StorageFileName);
            string outputFolderPath = Path.Combine(this.outputBasePath, testCaseFolder);

            if (!Directory.Exists(outputFolderPath))
                Directory.CreateDirectory(outputFolderPath);
            else
                EmptyFolder(outputFolderPath);

            string actualIndexPath = Path.Combine(outputFolderPath, BinaryStorage.IndexFileName);
            string actualStoragePath = Path.Combine(outputFolderPath, BinaryStorage.StorageFileName);
            // Act
            using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = outputFolderPath }))
            {
                Directory.GetFiles(inputFolderPath, "*", SearchOption.AllDirectories).ToList()
                    .ForEach(s =>
                    {
                        string fileName = Path.GetFileName(s);
                        if (isNetwork)
                            AddNetworkStream(storage, s, fileName);
                        else
                            AddFileStream(storage, s, fileName); ;
                    });
            }
            // Assert
            byte[] expectedIndexBytes = File.ReadAllBytes(assertIndexPath);
            byte[] actualIndexBytes = File.ReadAllBytes(actualIndexPath);
            byte[] expectedStorageBytes = File.ReadAllBytes(assertStoragePath);
            byte[] actualStorageBytes = File.ReadAllBytes(actualStoragePath);

            Assert.IsTrue(expectedIndexBytes.SequenceEqual(actualIndexBytes));
            Assert.IsTrue(expectedStorageBytes.SequenceEqual(actualStorageBytes));
        }

        private void AddFileStream(IBinaryStorage storage, string filePath, string key, StreamInfo info = null)
        {
            using (var file = new FileStream(filePath, FileMode.Open))
            {
                if (info == null)
                    info = StreamInfo.Empty;
                storage.Add(key, file, info);
            }
        }

        private void AddNetworkStream(IBinaryStorage storage, string filePath, string key)
        {
            byte[] data = File.ReadAllBytes(filePath);
            using (var networkStream = new NetworkStreamMock())
            {
                networkStream.Write(data, 0, data.Length);
                StreamInfo info = StreamInfo.Empty;
                storage.Add(key, networkStream, info);
            }
        }

        private void EmptyFolder(string path)
        {
            System.IO.DirectoryInfo di = new DirectoryInfo(path);
            foreach (FileInfo file in di.EnumerateFiles())
            {
                file.Delete();
            }
            foreach (DirectoryInfo dir in di.EnumerateDirectories())
            {
                dir.Delete(true);
            }
        }

        private static bool AreStreamsEqual(string sourceName, FileStream sourceStream, Stream resultStream)
        {
            if (sourceStream.Length != resultStream.Length)
            {
                return false;
            }

            byte[] hash1, hash2;
            using (MD5 md5 = MD5.Create())
            {
                hash1 = md5.ComputeHash(sourceStream);

                md5.Initialize();
                hash2 = md5.ComputeHash(resultStream);
            }

            if (!hash1.SequenceEqual(hash2))
            {
                return false;
            }

            return true;
        }
        #endregion
    }
}
