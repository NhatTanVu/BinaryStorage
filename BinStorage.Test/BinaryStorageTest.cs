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
        private readonly string inputFolder = ".\\_input";
        private readonly string assertFolder = ".\\_assert";
        private readonly string outputFolder = ".\\_output";
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
            string shouldGetFolder = "ShouldNotAddDuplicateKey";
            string inputFolderPath = Path.Combine(inputFolder, shouldGetFolder);
            string assertFileName = "16ddb0c9-9a88-47f2-a582-f7f09322506b.bin";
            string assertFilePath = Path.Combine(assertFolder, shouldGetFolder, assertFileName);
            // Act
            using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = inputFolderPath }))
            {
                AddFile(storage, assertFilePath, assertFileName);
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
            string shouldGetFolder = "ShouldGet";
            string inputFolderPath = Path.Combine(inputFolder, shouldGetFolder);
            string assertFolderPath = Path.Combine(assertFolder, shouldGetFolder);
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
                                if (sourceStream.Length != resultStream.Length)
                                {
                                    throw new Exception(string.Format("Length did not match: Source - '{0}', Result - {1}", sourceStream.Length, resultStream.Length));
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
                                    throw new Exception(string.Format("Hashes do not match for file - '{0}'  ", s));
                                }
                            }
                        }
                    });
            }
            // Assert
            Assert.IsTrue(true);
        }

        [TestMethod]
        public void ShouldContain()
        {
            // Arrange
            string shouldContainFolder = "ShouldGet";
            string inputFolderPath = Path.Combine(inputFolder, shouldContainFolder);
            string assertFolderPath = Path.Combine(assertFolder, shouldContainFolder);
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
        public void ShouldNotCompress()
        {
            // Arrange
            string shouldNotCompress = "ShouldNotCompress";
            string inputFolderPath = Path.Combine(inputFolder, shouldNotCompress);
            string outputFolderPath = Path.Combine(outputFolder, shouldNotCompress);
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
                        AddFile(storage, s, fileName);
                    });
            }
            // Assert
            long actualLength = new FileInfo(outputStoragePath).Length;
            Assert.AreEqual(assertLength, actualLength);
        }

        #region Private Methods
        private void AddAndCheck(string testCaseFolder)
        {
            // Arrange
            string inputFolderPath = Path.Combine(inputFolder, testCaseFolder);
            string assertIndexPath = Path.Combine(assertFolder, testCaseFolder, BinaryStorage.IndexFileName);
            string assertStoragePath = Path.Combine(assertFolder, testCaseFolder, BinaryStorage.StorageFileName);
            string outputFolderPath = Path.Combine(outputFolder, testCaseFolder);

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
                        AddFile(storage, s, fileName);
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

        private void AddFile(IBinaryStorage storage, string filePath, string key)
        {
            using (var file = new FileStream(filePath, FileMode.Open))
            {
                StreamInfo info = StreamInfo.Empty;
                storage.Add(key, file, info);
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
        #endregion
    }
}
