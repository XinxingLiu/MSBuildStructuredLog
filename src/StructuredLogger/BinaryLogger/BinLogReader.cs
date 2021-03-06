﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Microsoft.Build.Framework;

namespace Microsoft.Build.Logging.StructuredLogger
{
    /// <summary>
    /// Provides a method to read a binary log file (*.binlog) and replay all stored BuildEventArgs
    /// by implementing IEventSource and raising corresponding events.
    /// </summary>
    /// <remarks>The class is public so that we can call it from MSBuild.exe when replaying a log file.</remarks>
    public sealed class BinLogReader : EventArgsDispatcher
    {
        /// <summary>
        /// Raised when the log reader encounters a binary blob embedded in the stream.
        /// The arguments include the blob kind and the byte buffer with the contents.
        /// </summary>
        public event Action<BinaryLogRecordKind, byte[]> OnBlobRead;

        /// <summary>
        /// Raised when there was an exception reading a record from the file.
        /// </summary>
        public event Action<Exception> OnException;

        /// <summary>
        /// Read the provided binary log file and raise corresponding events for each BuildEventArgs
        /// </summary>
        /// <param name="sourceFilePath">The full file path of the binary log file</param>
        public void Replay(string sourceFilePath)
        {
            using (var stream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Replay(stream);
            }
        }

        public void Replay(Stream stream)
        {
            var gzipStream = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            var binaryReader = new BinaryReader(gzipStream);

            int fileFormatVersion = binaryReader.ReadInt32();

            // the log file is written using a newer version of file format
            // that we don't know how to read
            if (fileFormatVersion > BinaryLogger.FileFormatVersion)
            {
                var text = $"Unsupported log file format. Latest supported version is {BinaryLogger.FileFormatVersion}, the log file has version {fileFormatVersion}.";
                throw new NotSupportedException(text);
            }

            // Use a producer-consumer queue so that IO can happen on one thread
            // while processing can happen on another thread decoupled. The speed
            // up is from 4.65 to 4.15 seconds.
            var queue = new BlockingCollection<BuildEventArgs>(boundedCapacity: 5000);
            var processingTask = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var args in queue.GetConsumingEnumerable())
                {
                    Dispatch(args);
                }
            });

            int recordsRead = 0;

            var reader = new BuildEventArgsReader(binaryReader, fileFormatVersion);
            reader.OnBlobRead += OnBlobRead;
            while (true)
            {
                BuildEventArgs instance = null;

                try
                {
                    instance = reader.Read();
                }
                catch (Exception ex)
                {
                    OnException?.Invoke(ex);
                }

                recordsRead++;
                if (instance == null)
                {
                    queue.CompleteAdding();
                    break;
                }

                queue.Add(instance);
            }

            processingTask.Wait();
        }

        private class DisposableEnumerable<T> : IEnumerable<T>, IDisposable
        {
            private IEnumerable<T> enumerable;
            private Action dispose;

            public static IEnumerable<T> Create(IEnumerable<T> enumerable, Action dispose)
            {
                return new DisposableEnumerable<T>(enumerable, dispose);
            }

            public DisposableEnumerable(IEnumerable<T> enumerable, Action dispose)
            {
                this.enumerable = enumerable;
                this.dispose = dispose;
            }

            public void Dispose()
            {
                if (dispose != null)
                {
                    dispose();
                    dispose = null;
                }
            }

            public IEnumerator<T> GetEnumerator() => enumerable.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => enumerable.GetEnumerator();
        }

        /// <summary>
        /// Enumerate over all records in the file. For each record store the bytes,
        /// the start position in the stream, length in bytes and the deserialized object.
        /// </summary>
        /// <remarks>Useful for debugging and analyzing binary logs</remarks>
        public IEnumerable<Record> ReadRecords(string logFilePath)
        {
            var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return DisposableEnumerable<Record>.Create(ReadRecords(stream), () => stream.Dispose());
        }

        public IEnumerable<Record> ReadRecords(byte[] bytes)
        {
            var stream = new MemoryStream(bytes);
            return ReadRecords(stream);
        }

        /// <summary>
        /// Enumerate over all records in the binary log stream. For each record store the bytes,
        /// the start position in the stream, length in bytes and the deserialized object.
        /// </summary>
        /// <remarks>Useful for debugging and analyzing binary logs</remarks>
        public IEnumerable<Record> ReadRecords(Stream binaryLogStream)
        {
            var gzipStream = new GZipStream(binaryLogStream, CompressionMode.Decompress, leaveOpen: true);
            return ReadRecordsFromDecompressedStream(gzipStream);
        }

        public IEnumerable<Record> ReadRecordsFromDecompressedStream(Stream decompressedStream)
        {
            var binaryReader = new BinaryReader(decompressedStream);

            int fileFormatVersion = binaryReader.ReadInt32();

            // the log file is written using a newer version of file format
            // that we don't know how to read
            if (fileFormatVersion > BinaryLogger.FileFormatVersion)
            {
                var text = $"Unsupported log file format. Latest supported version is {BinaryLogger.FileFormatVersion}, the log file has version {fileFormatVersion}.";
                throw new NotSupportedException(text);
            }

            long lengthOfBlobsAddedLastTime = 0;

            List<Record> blobs = new List<Record>();

            var reader = new BuildEventArgsReader(binaryReader, fileFormatVersion);
            reader.OnBlobRead += (kind, blob) =>
            {
                var record = new Record
                {
                    Bytes = blob,
                    Args = null,
                    Start = 0, // TODO: see if we can re-add that
                    Length = blob.Length
                };

                blobs.Add(record);
                lengthOfBlobsAddedLastTime += blob.Length;
            };

            while (true)
            {
                BuildEventArgs instance = null;

                instance = reader.Read();
                if (instance == null)
                {
                    break;
                }

                var record = new Record
                {
                    Bytes = null, // probably can reconstruct this from the Args if necessary
                    Args = instance,
                    Start = 0,
                    Length = 0
                };

                yield return record;

                lengthOfBlobsAddedLastTime = 0;
            }

            foreach (var blob in blobs)
            {
                yield return blob;
            }
        }
    }
}
