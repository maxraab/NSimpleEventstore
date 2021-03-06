﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using nsimpleeventstore.adapters;
using nsimpleeventstore.adapters.eventrepositories;
using nsimpleeventstore.contract;

namespace nsimpleeventstore
{
    /*
     * Events are stored by expanding a persistent array incrementally per event.
     * 
     * The chronological sequence of events is maintained by numbering events in their chronological order
     * starting with 0.
     *
     * The event store is thread-safe; only one batch of events gets processed a at time.
     * (Many threads can replay events, though, but only one can record new ones.)
     * There must be only one event store instance for a certain event store path within a process.
     * Several processes working on the same event store path need to share an event store instance via
     * a common event store server process. Only that way the consistency of the events stored in a path
     * can be guaranteed.
     *
     * The event store is versioned. The version number is opaque to clients; they should not expect version numbers
     * to be ordered in any way or increase over time. The version changes whenever events got recorded.
     *
     * Storing a batch of events is not transactional. But due to the thread-safety of the event store and the
     * simplicity of the persistence approach it is assumed that failure during writing the events to files
     * is very unlikely.
     */
    public abstract class Eventstore<T> : IEventstore where T : IEventRepository
    {
        private const string DEFAUL_PATH = "eventstore.db";
        
        public event Action<string, long, Event[]> OnRecorded = (v,f,e) => { };

        private readonly Lock _lock;
        private readonly IEventRepository _repo;        
        
        protected Eventstore() : this(DEFAUL_PATH) {}
        protected Eventstore(string path) {
            _repo = (T)Activator.CreateInstance(typeof(T), path);
            _lock = new Lock();
        }
        
        public (string Version, long FinalEventNumber) Record(Event e, string expectedVersion="") => Record(new[] {e}, expectedVersion);
        public (string Version, long FinalEventNumber) Record(Event[] events, string expectedVersion="") {
            try
            {                
                _lock.TryWrite(() => {
                    var n = _repo.Count;
                    var currentVersion = n.ToString();

                    Check_for_version_conflict(currentVersion);
                    Store_all_events(n);
                });

                var (version, finalEventNumber) = State;
                OnRecorded(version, finalEventNumber, events);
                return (version, finalEventNumber);
            } finally{}


            void Check_for_version_conflict(string currentVersion) {
                if (!string.IsNullOrEmpty(expectedVersion) && 
                    expectedVersion != currentVersion) throw new VersionNotFoundException($"Event store version conflict! Version '{expectedVersion}' expected, but is '{currentVersion}'!");
            }

            void Store_all_events(long index) => events.ToList().ForEach(e => _repo.Store(index++, e));
        }

        public (string Version, Event[] Events) Replay(long firstEventNumber = -1) => Replay(firstEventNumber, new Type[0]);
        public (string Version, Event[] Events) Replay(params Type[] eventTypes) => Replay(-1, eventTypes);
        public (string Version, Event[] Events) Replay(long firstEventNumber, params Type[] eventTypes)
        {
            return _lock.TryRead(
                () => (_repo.Count.ToString(),
                       Filter(AllEvents()).ToArray()));


            IEnumerable<Event> AllEvents() {
                var n = _repo.Count;
                for (var i = firstEventNumber < 0 ? 0 : firstEventNumber; i < n; i++)
                    yield return _repo.Load(i);
            }

            IEnumerable<Event> Filter(IEnumerable<Event> events) {
                if (eventTypes.Length <= 0) return events;
                
                var eventTypes_ = new HashSet<Type>(eventTypes);
                return events.Where(e => eventTypes_.Contains(e.GetType()));
            }
        }
        
        public (string Version, long FinalEventNumber) State
            => _lock.TryRead(() =>  {
                var n = _repo.Count;
                return (n.ToString(), n - 1);
            });

        public string Path => _repo.Path;        
        
        public void Dispose() { _repo.Dispose(); }
    }
}