using BinStorage.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BinStorage.TestApp
{
	class Program
	{
        static void Main(string[] args)
		{
            string sourceFolder = @"C:\_code\BinaryStorage\_input";
            string destFolder = @"C:\_code\BinaryStorage\_output";
            int degreeOfParallelism = 12;
            int count = 0;

            // Create storage and add data
            Console.WriteLine("Creating storage from " + sourceFolder);
			Stopwatch sw = Stopwatch.StartNew();
            using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = destFolder }))
            {
                Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
                    .AsParallel().WithDegreeOfParallelism(degreeOfParallelism).ForAll(s =>
                    {
                        AddFile(storage, s);
                    });
            }
            Console.WriteLine("Time to create: " + sw.Elapsed);

            // Open storage and read data
            Console.WriteLine("Verifying data");
            sw = Stopwatch.StartNew();
			using (var storage = new BinaryStorage(new StorageConfiguration() { WorkingFolder = destFolder }))
			{
                Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
                    .AsParallel().WithDegreeOfParallelism(degreeOfParallelism).ForAll(s =>
                    {
						using (var resultStream = storage.Get(s)) {
							using (var sourceStream = new FileStream(s, FileMode.Open, FileAccess.Read)) {
								if (sourceStream.Length != resultStream.Length) {
									throw new Exception(string.Format("Length did not match: Source - '{0}', Result - {1}", sourceStream.Length, resultStream.Length));
								}

								byte[] hash1, hash2;
								using (MD5 md5 = MD5.Create()) {
									hash1 = md5.ComputeHash(sourceStream);

									md5.Initialize();
									hash2 = md5.ComputeHash(resultStream);
								}

								if (!hash1.SequenceEqual(hash2)) {
									throw new Exception(string.Format("Hashes do not match for file - '{0}'  ", s));
								}
							}
						}
                        Interlocked.Add(ref count, 1);
                    });
                Console.WriteLine("Time to verify: " + sw.Elapsed + ", count = " + count);
                ObjectSerializer.ClearTempFiles();

                Console.WriteLine("Verifying data 2nd time");
                sw = Stopwatch.StartNew();
                count = 0;

                Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
                    .AsParallel().WithDegreeOfParallelism(degreeOfParallelism).ForAll(s =>
                    {
                        using (var resultStream = storage.Get(s))
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
                        Interlocked.Add(ref count, 1);
                    });
                Console.WriteLine("Time to verify: " + sw.Elapsed + ", count = " + count);
            }

            Console.ReadLine();
		}

		static void AddFile(IBinaryStorage storage, string fileName)
		{
			using (var file = new FileStream(fileName, FileMode.Open))
			{
                StreamInfo info = StreamInfo.Empty;
                storage.Add(fileName, file, info);
			}
		}

		static void AddBytes(IBinaryStorage storage, string key, byte[] data)
		{
			StreamInfo streamInfo = new StreamInfo();
			using (MD5 md5 = MD5.Create())
			{
				streamInfo.Hash = md5.ComputeHash(data);
			}
			streamInfo.Length = data.Length;
			streamInfo.IsCompressed = false;

			using (var ms = new MemoryStream(data))
			{
				storage.Add(key, ms, streamInfo);
			}
		}

		static void Dump(IBinaryStorage storage, string key, string fileName)
		{
			using (var file = new FileStream(fileName, FileMode.Create))
			{
				storage.Get(key).CopyTo(file);
			}
		}
	}
}
