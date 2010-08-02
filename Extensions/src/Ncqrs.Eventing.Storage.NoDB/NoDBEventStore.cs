﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Ncqrs.Eventing.Sourcing;
using Ncqrs.Eventing.Storage.Serialization;
using Newtonsoft.Json.Linq;

namespace Ncqrs.Eventing.Storage.NoDB
{
    public class NoDBEventStore : IEventStore
    {
        private readonly JsonEventFormatter _formatter;
        private readonly string _path;

        public NoDBEventStore(string path)
        {
            _path = path;
            _formatter = new JsonEventFormatter(new SimpleEventTypeResolver());
        }

        #region IEventStore Members

        public IEnumerable<SourcedEvent> GetAllEvents(Guid id)
        {
            return GetAllEventsSinceVersion(id, 0);
        }

        public IEnumerable<SourcedEvent> GetAllEventsSinceVersion(Guid id, long version)
        {
            FileInfo file = GetEventSourceFileInfo(id);
            if (!file.Exists) yield break;
            GetReadLock(id);
            using (StreamReader reader = file.OpenText())
            {
                reader.ReadLine(); //Throw away the version line
                string line = reader.ReadLine();
                int i = 0;
                while (line != null)
                {
                    if (i >= version)
                        yield return (SourcedEvent) _formatter.Deserialize(line.ReadStoredEvent(id, i));
                    line = reader.ReadLine();
                    i++;
                }
            }
            ReleaseReadLock(id);
        }

        public void Save(IEventSource source)
        {
            FileInfo file = GetEventSourceFileInfo(source.EventSourceId);
            if (!file.Exists && !file.Directory.Exists)
                file.Directory.Create();
            GetWriteLock(source.EventSourceId);
            if (file.Exists)
            {
                if (GetVersion(file) > source.InitialVersion)
                    throw new ConcurrencyException(source.EventSourceId, source.Version);
            }
            using (StreamWriter writer = file.AppendText())
            {
                writer.AutoFlush = false;
                UpdateEventSourceVersion(writer, source.Version);
                foreach (SourcedEvent sourcedEvent in source.GetUncommittedEvents())
                {
                    StoredEvent<JObject> storedEvent = _formatter.Serialize(sourcedEvent);
                    writer.WriteLine(storedEvent.WriteLine());
                }
                writer.Flush();
            }
            ReleaseWriteLock(source.EventSourceId);
        }

        private void UpdateEventSourceVersion(StreamWriter writer, long version)
        {
            var versionstring = version.ToString("00000000000000000000") + writer.NewLine;
            Stream filestream = writer.BaseStream;
            filestream.Position = 0;
            filestream.Write(Encoding.UTF8.GetBytes(versionstring), 0, versionstring.Length);
            filestream.Position = filestream.Length;
        }

        private static long GetVersion(FileInfo file)
        {
            using (var reader = file.OpenText())
            {
                string readLine = reader.ReadLine();
                return long.Parse(readLine);
            }
        }

        #endregion

        private FileInfo GetEventSourceFileInfo(Guid eventSourceId)
        {
            string foldername = eventSourceId.ToString().Substring(0, 2);
            string filename = eventSourceId.ToString().Substring(2);
            string path = Path.Combine(_path, foldername, filename);
            return new FileInfo(path);
        }

        const int maxReaders = 10;

        private static void GetWriteLock(Guid id)
        {
            var mutex = new Mutex(false, id.ToString() + "write");
            mutex.WaitOne();
            var sem = new Semaphore(maxReaders, maxReaders, id.ToString());
            int readlocks = 0;
            while (readlocks < maxReaders)
            {
                sem.WaitOne();
                readlocks++;
            }
            mutex.ReleaseMutex();
        }

        private static void ReleaseWriteLock(Guid id)
        {
            var sem = new Semaphore(maxReaders, maxReaders, id.ToString());
            sem.Release(maxReaders);
        }

        private static void GetReadLock(Guid id)
        {
            var sem = new Semaphore(maxReaders, maxReaders, id.ToString());
            sem.WaitOne();
        }

        private static void ReleaseReadLock(Guid id)
        {
            var sem = new Semaphore(maxReaders, maxReaders, id.ToString());
            sem.WaitOne();
        }
    }

    public static class StoredEventExtensions
    {
        public static string WriteLine(this StoredEvent<JObject> storedEvent)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("{0};{1};{2};{3};{4};",
                            storedEvent.EventIdentifier, storedEvent.EventTimeStamp.Ticks, storedEvent.EventName,
                            storedEvent.EventVersion, storedEvent.Data.ToString().Replace("\n", "").Replace("\r", ""));
            return sb.ToString();
        }

        public static StoredEvent<JObject> ReadStoredEvent(this string eventString, Guid id, long version)
        {
            string[] data = eventString.Split(';');
            return new StoredEvent<JObject>(new Guid(data[0]), new DateTime(long.Parse(data[1])), data[2],
                                            new Version(data[3]), id, version,
                                            JObject.Parse(data[4]));
        }
    }
}