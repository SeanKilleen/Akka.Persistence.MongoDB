﻿//-----------------------------------------------------------------------
// <copyright file="MongoDbSnapshotStore.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Akka.Persistence.Snapshot;
using Akka.Serialization;
using Akka.Util;
using MongoDB.Driver;

namespace Akka.Persistence.MongoDb.Snapshot
{
    /// <summary>
    /// A SnapshotStore implementation for writing snapshots to MongoDB.
    /// </summary>
    public class MongoDbSnapshotStore : SnapshotStore
    {
        private readonly MongoDbSnapshotSettings _settings;

        private Lazy<IMongoCollection<SnapshotEntry>> _snapshotCollection;

        private readonly Func<object, SerializationResult> _serialize;
        private readonly Func<Type, object, string, int?, object> _deserialize;

        public MongoDbSnapshotStore()
        {
            _settings = MongoDbPersistence.Get(Context.System).SnapshotStoreSettings;

            var serialization = Context.System.Serialization;
            switch (_settings.StoredAs)
            {
                case StoredAsType.Binary:
                    _serialize = o =>
                    {
                        var serializer = serialization.FindSerializerFor(o);
                        return new SerializationResult(serializer.ToBinary(o), serializer);
                    };
                    _deserialize = (type, serialized, manifest, serializerId) =>
                    {
                        if (serializerId.HasValue)
                        {
                            /*
                             * Backwards compat: check to see if manifest is populated before using it.
                             * Otherwise, fall back to using the stored type data instead.
                             * Per: https://github.com/AkkaNetContrib/Akka.Persistence.MongoDB/issues/57
                             */
                            if (string.IsNullOrEmpty(manifest))
                                return serialization.Deserialize((byte[])serialized, serializerId.Value, type);
                            return serialization.Deserialize((byte[])serialized, serializerId.Value, manifest);
                        }

                        var deserializer = serialization.FindSerializerForType(type);
                        return deserializer.FromBinary((byte[]) serialized, type);
                    };
                    break;
                default:
                    _serialize = o => new SerializationResult(o, null);
                    _deserialize = (type, serialized, manifest, serializerId) => serialized;
                    break;
            }
        }

        protected override void PreStart()
        {
            base.PreStart();

            _snapshotCollection = new Lazy<IMongoCollection<SnapshotEntry>>(() =>
            {
                var connectionString = new MongoUrl(_settings.ConnectionString);
                var client = new MongoClient(connectionString);

                var snapshot = client.GetDatabase(connectionString.DatabaseName);

                var collection = snapshot.GetCollection<SnapshotEntry>(_settings.Collection);
                if (_settings.AutoInitialize)
                {
                    collection.Indexes.CreateOneAsync(
                        Builders<SnapshotEntry>.IndexKeys
                        .Ascending(entry => entry.PersistenceId)
                        .Descending(entry => entry.SequenceNr))
                        .Wait();
                }

                return collection;
            });
        }

        protected override Task<SelectedSnapshot> LoadAsync(string persistenceId, SnapshotSelectionCriteria criteria)
        {
            var filter = CreateRangeFilter(persistenceId, criteria);

            return
                _snapshotCollection.Value
                    .Find(filter)
                    .SortByDescending(x => x.SequenceNr)
                    .Limit(1)
                    .Project(x => ToSelectedSnapshot(x))
                    .FirstOrDefaultAsync();
        }

        protected override async Task SaveAsync(SnapshotMetadata metadata, object snapshot)
        {
            var snapshotEntry = ToSnapshotEntry(metadata, snapshot);

            await _snapshotCollection.Value.ReplaceOneAsync(
                CreateSnapshotIdFilter(snapshotEntry.Id),
                snapshotEntry,
                new UpdateOptions { IsUpsert = true });
        }

        protected override Task DeleteAsync(SnapshotMetadata metadata)
        {
            var builder = Builders<SnapshotEntry>.Filter;
            var filter = builder.Eq(x => x.PersistenceId, metadata.PersistenceId);

            if (metadata.SequenceNr > 0 && metadata.SequenceNr < long.MaxValue)
                filter &= builder.Eq(x => x.SequenceNr, metadata.SequenceNr);

            if (metadata.Timestamp != DateTime.MinValue && metadata.Timestamp != DateTime.MaxValue)
                filter &= builder.Eq(x => x.Timestamp, metadata.Timestamp.Ticks);

            return _snapshotCollection.Value.FindOneAndDeleteAsync(filter);
        }

        protected override Task DeleteAsync(string persistenceId, SnapshotSelectionCriteria criteria)
        {
            var filter = CreateRangeFilter(persistenceId, criteria);

            return _snapshotCollection.Value.DeleteManyAsync(filter);
        }

        private static FilterDefinition<SnapshotEntry> CreateSnapshotIdFilter(string snapshotId)
        {
            var builder = Builders<SnapshotEntry>.Filter;

            var filter = builder.Eq(x => x.Id, snapshotId);

            return filter;
        }

        private static FilterDefinition<SnapshotEntry> CreateRangeFilter(string persistenceId, SnapshotSelectionCriteria criteria)
        {
            var builder = Builders<SnapshotEntry>.Filter;
            var filter = builder.Eq(x => x.PersistenceId, persistenceId);

            if (criteria.MaxSequenceNr > 0 && criteria.MaxSequenceNr < long.MaxValue)
                filter &= builder.Lte(x => x.SequenceNr, criteria.MaxSequenceNr);

            if (criteria.MaxTimeStamp != DateTime.MinValue && criteria.MaxTimeStamp != DateTime.MaxValue)
                filter &= builder.Lte(x => x.Timestamp, criteria.MaxTimeStamp.Ticks);

            return filter;
        }

        private SnapshotEntry ToSnapshotEntry(SnapshotMetadata metadata, object snapshot)
        {
            var serializationResult = _serialize(snapshot);
            var serializer = serializationResult.Serializer;
            var hasSerializer = serializer != null;

            var manifest = "";
            if (hasSerializer && serializer is SerializerWithStringManifest)
                manifest = ((SerializerWithStringManifest)serializer).Manifest(snapshot);
            else if (hasSerializer && serializer.IncludeManifest)
                manifest = snapshot.GetType().TypeQualifiedName();
            else
                manifest = snapshot.GetType().TypeQualifiedName();

            return new SnapshotEntry
            {
                Id = metadata.PersistenceId + "_" + metadata.SequenceNr,
                PersistenceId = metadata.PersistenceId,
                SequenceNr = metadata.SequenceNr,
                Snapshot = serializationResult.Payload,
                Timestamp = metadata.Timestamp.Ticks,
                Manifest = manifest,
                SerializerId = serializer?.Identifier
            };
        }

        private SelectedSnapshot ToSelectedSnapshot(SnapshotEntry entry)
        {
            Type type = null;

            if (!string.IsNullOrEmpty(entry.Manifest))
                type = Type.GetType(entry.Manifest, throwOnError: true);

            var snapshot = _deserialize(type, entry.Snapshot, entry.Manifest, entry.SerializerId);

            return new SelectedSnapshot(
                new SnapshotMetadata(entry.PersistenceId, entry.SequenceNr, new DateTime(entry.Timestamp)), snapshot);
        }
    }
}
