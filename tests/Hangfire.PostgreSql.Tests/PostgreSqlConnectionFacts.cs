﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using Dapper;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Moq;
using Npgsql;
using Xunit;

namespace Hangfire.PostgreSql.Tests
{
    public class PostgreSqlConnectionFacts
    {
        private readonly Mock<IPersistentJobQueue> _queue;
        private readonly Mock<IPersistentJobQueueProvider> _provider;
        private readonly PersistentJobQueueProviderCollection _providers;

        public PostgreSqlConnectionFacts()
        {
            _queue = new Mock<IPersistentJobQueue>();

            _provider = new Mock<IPersistentJobQueueProvider>();
            _provider.Setup(x => x.GetJobQueue(It.IsNotNull<IDbConnection>()))
                .Returns(_queue.Object);

            _providers = new PersistentJobQueueProviderCollection(_provider.Object);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenConnectionIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new PostgreSqlConnection(null, _providers));

            Assert.Equal("connection", exception.ParamName);
        }

        [Fact, CleanDatabase]
        public void Ctor_ThrowsAnException_WhenProvidersCollectionIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new PostgreSqlConnection(ConnectionUtils.CreateConnection(), null));

            Assert.Equal("queueProviders", exception.ParamName);
        }

        [Fact, CleanDatabase]
        public void FetchNextJob_DelegatesItsExecution_ToTheQueue()
        {
            UseConnection(connection =>
            {
                var token = new CancellationToken();
                var queues = new[] { "default" };

                connection.FetchNextJob(queues, token);

                _queue.Verify(x => x.Dequeue(queues, token));
            });
        }

        [Fact, CleanDatabase]
        public void FetchNextJob_Throws_IfMultipleProvidersResolved()
        {
            UseConnection(connection =>
            {
                var token = new CancellationToken();
                var anotherProvider = new Mock<IPersistentJobQueueProvider>();
                _providers.Add(anotherProvider.Object, new [] { "critical" });

                Assert.Throws<InvalidOperationException>(
                    () => connection.FetchNextJob(new[] { "critical", "default" }, token));
            });
        }

        [Fact, CleanDatabase]
        public void CreateWriteTransaction_ReturnsNonNullInstance()
        {
            UseConnection(connection =>
            {
                var transaction = connection.CreateWriteTransaction();
                Assert.NotNull(transaction);
            });
        }

        [Fact, CleanDatabase]
        public void AcquireLock_ReturnsNonNullInstance()
        {
            UseConnection(connection =>
            {
                var @lock = connection.AcquireDistributedLock("1", TimeSpan.FromSeconds(1));
                Assert.NotNull(@lock);
            });
        }

        [Fact, CleanDatabase]
        public void CreateExpiredJob_ThrowsAnException_WhenJobIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.CreateExpiredJob(
                        null,
                        new Dictionary<string, string>(),
                        DateTime.UtcNow,
                        TimeSpan.Zero));

                Assert.Equal("job", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void CreateExpiredJob_ThrowsAnException_WhenParametersCollectionIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.CreateExpiredJob(
                        Job.FromExpression(() => SampleMethod("hello")),
                        null,
                        DateTime.UtcNow,
                        TimeSpan.Zero));

                Assert.Equal("parameters", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void CreateExpiredJob_CreatesAJobInTheStorage_AndSetsItsParameters()
        {
            UseConnections((sql, connection) =>
            {
                var createdAt = new DateTime(2012, 12, 12);
                var jobId = connection.CreateExpiredJob(
                    Job.FromExpression(() => SampleMethod("Hello")),
                    new Dictionary<string, string> { { "Key1", "Value1" }, { "Key2", "Value2" } },
                    createdAt,
                    TimeSpan.FromDays(1));

                Assert.NotNull(jobId);
                Assert.NotEmpty(jobId);

                var sqlJob = sql.Query(@"select * from ""hangfire"".""job""").Single();
                Assert.Equal(jobId, sqlJob.id.ToString());
                Assert.Equal(createdAt, sqlJob.createdat);
                Assert.Equal(null, (int?) sqlJob.stateid);
                Assert.Equal(null, (string) sqlJob.statename);

                var invocationData = JobHelper.FromJson<InvocationData>((string)sqlJob.invocationdata);
                invocationData.Arguments = sqlJob.arguments;

                var job = invocationData.Deserialize();
                Assert.Equal(typeof(PostgreSqlConnectionFacts), job.Type);
                Assert.Equal("SampleMethod", job.Method.Name);
                Assert.Equal("\"Hello\"", job.Arguments[0]);

                Assert.True(createdAt.AddDays(1).AddMinutes(-1) < sqlJob.expireat);
                Assert.True(sqlJob.expireat < createdAt.AddDays(1).AddMinutes(1));

                var parameters = sql.Query(
                    @"select * from ""hangfire"".""jobparameter"" where ""jobid"" = @id",
                    new { id = Convert.ToInt32(jobId, CultureInfo.InvariantCulture) })
                    .ToDictionary(x => (string) x.name, x => (string) x.value);

                Assert.Equal("Value1", parameters["Key1"]);
                Assert.Equal("Value2", parameters["Key2"]);
            });
        }

        [Fact, CleanDatabase]
        public void GetJobData_ThrowsAnException_WhenJobIdIsNull()
        {
            UseConnection(connection => Assert.Throws<ArgumentNullException>(
                    () => connection.GetJobData(null)));
        }

        [Fact, CleanDatabase]
        public void GetJobData_ReturnsNull_WhenThereIsNoSuchJob()
        {
            UseConnection(connection =>
            {
                var result = connection.GetJobData("1");
                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetJobData_ReturnsResult_WhenJobExists()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""job"" (""invocationdata"", ""arguments"", ""statename"", ""createdat"")
values (@invocationData, @arguments, @stateName, now() at time zone 'utc') returning ""id""";

            UseConnections((sql, connection) =>
            {
                var job = Job.FromExpression(() => SampleMethod("wrong"));

                var jobId = sql.Query(
                    arrangeSql,
                    new
                    {
                        invocationData = JobHelper.ToJson(InvocationData.Serialize(job)),
                        stateName = "Succeeded",
                        arguments = "['Arguments']"
                    }).Single();

                var result = connection.GetJobData(((int)jobId.id).ToString());

                Assert.NotNull(result);
                Assert.NotNull(result.Job);
                Assert.Equal("Succeeded", result.State);
                Assert.Equal("Arguments", result.Job.Arguments[0]);
                Assert.Null(result.LoadException);
                Assert.True(DateTime.UtcNow.AddMinutes(-1) < result.CreatedAt);
                Assert.True(result.CreatedAt < DateTime.UtcNow.AddMinutes(1));
            });
        }

        [Fact, CleanDatabase]
        public void GetStateData_ThrowsAnException_WhenJobIdIsNull()
        {
            UseConnection(
                connection => Assert.Throws<ArgumentNullException>(
                    () => connection.GetStateData(null)));
        }

        [Fact, CleanDatabase]
        public void GetStateData_ReturnsNull_IfThereIsNoSuchState()
        {
            UseConnection(connection =>
            {
                var result = connection.GetStateData("1");
                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetStateData_ReturnsCorrectData()
        {
            const string createJobSql = @"
INSERT INTO ""hangfire"".""job"" (""invocationdata"", ""arguments"", ""statename"", ""createdat"")
	VALUES ('', '', '', now() at time zone 'utc') RETURNING ""id"";
            ";

            const string createStateSql = @"
insert into ""hangfire"".""state"" (""jobid"", ""name"", ""createdat"")
VALUES(@jobId, 'old-state', now() at time zone 'utc');

insert into ""hangfire"".""state"" (""jobid"", ""name"", ""reason"", ""data"", ""createdat"")
VALUES(@jobId, @name, @reason, @data, now() at time zone 'utc')
returning ""id"";";

            const string updateJobStateSql = @"
	update ""hangfire"".""job""
	set ""stateid"" = @stateId
    where ""id"" = @jobId;
";

            UseConnections((sql, connection) =>
            {

                var data = new Dictionary<string, string>
                {
                    { "Key", "Value" }
                };

                var jobId = (int)sql.Query(createJobSql).Single().id;

                var stateId = (int)sql.Query(
                    createStateSql,
                    new { jobId = jobId, name = "Name", reason = "Reason", @data = JobHelper.ToJson(data) }).Single().id;

                sql.Execute(updateJobStateSql, new {jobId = jobId, stateId = stateId});

                var result = connection.GetStateData(jobId.ToString(CultureInfo.InvariantCulture));
                Assert.NotNull(result);

                Assert.Equal("Name", result.Name);
                Assert.Equal("Reason", result.Reason);
                Assert.Equal("Value", result.Data["Key"]);
            });
        }

        [Fact, CleanDatabase]
        public void GetJobData_ReturnsJobLoadException_IfThereWasADeserializationException()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""job"" (""invocationdata"", ""arguments"", ""statename"", ""createdat"")
values (@invocationData, @arguments, @stateName, now() at time zone 'utc') returning ""id""";

            UseConnections((sql, connection) =>
            {
                var jobId = sql.Query(
                    arrangeSql,
                    new
                    {
                        invocationData = JobHelper.ToJson(new InvocationData(null, null, null, null)),
                        stateName = "Succeeded",
                        arguments = "['Arguments']"
                    }).Single();

                var result = connection.GetJobData(((int)jobId.id).ToString());

                Assert.NotNull(result.LoadException);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameter_ThrowsAnException_WhenJobIdIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetJobParameter(null, "name", "value"));

                Assert.Equal("id", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameter_ThrowsAnException_WhenNameIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetJobParameter("1", null, "value"));

                Assert.Equal("name", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameters_CreatesNewParameter_WhenParameterWithTheGivenNameDoesNotExists()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""job"" (""invocationdata"", ""arguments"", ""createdat"")
values ('', '', now() at time zone 'utc') returning ""id""";

            UseConnections((sql, connection) =>
            {
                var job = sql.Query(arrangeSql).Single();
                string jobId = job.id.ToString();

                connection.SetJobParameter(jobId, "Name", "Value");

                var parameter = sql.Query(
                    @"select * from ""hangfire"".""jobparameter"" where ""jobid"" = @id and ""name"" = @name",
                    new { id = Convert.ToInt32(jobId, CultureInfo.InvariantCulture), name = "Name" }).Single();

                Assert.Equal("Value", parameter.value);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameter_UpdatesValue_WhenParameterWithTheGivenName_AlreadyExists()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""job"" (""invocationdata"", ""arguments"", ""createdat"")
values ('', '', now() at time zone 'utc') returning ""id""";

            UseConnections((sql, connection) =>
            {
                var job = sql.Query(arrangeSql).Single();
                string jobId = job.id.ToString();

                connection.SetJobParameter(jobId, "Name", "Value");
                connection.SetJobParameter(jobId, "Name", "AnotherValue");

                var parameter = sql.Query(
                    @"select * from ""hangfire"".""jobparameter"" where ""jobid"" = @id and ""name"" = @name",
                    new { id = Convert.ToInt32(jobId, CultureInfo.InvariantCulture), name = "Name" }).Single();

                Assert.Equal("AnotherValue", parameter.value);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameter_CanAcceptNulls_AsValues()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""job"" (""invocationdata"", ""arguments"", ""createdat"")
values ('', '', now() at time zone 'utc') returning ""id""";

            UseConnections((sql, connection) =>
            {
                var job = sql.Query(arrangeSql).Single();
                string jobId = job.id.ToString();

                connection.SetJobParameter(jobId, "Name", null);

                var parameter = sql.Query(
                    @"select * from ""hangfire"".""jobparameter"" where ""jobid"" = @id and ""name"" = @name",
                    new { id = Convert.ToInt32(jobId, CultureInfo.InvariantCulture), name = "Name" }).Single();

                Assert.Equal((string) null, parameter.value);
            });
        }

        [Fact, CleanDatabase]
        public void GetParameter_ThrowsAnException_WhenJobIdIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetJobParameter(null, "hello"));

                Assert.Equal("id", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetParameter_ThrowsAnException_WhenNameIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetJobParameter("1", null));

                Assert.Equal("name", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetParameter_ReturnsNull_WhenParameterDoesNotExists()
        {
            UseConnection(connection =>
            {
                var value = connection.GetJobParameter("1", "hello");
                Assert.Null(value);
            });
        }

        [Fact, CleanDatabase]
        public void GetParameter_ReturnsParameterValue_WhenJobExists()
        {
            const string arrangeSql = @"
WITH ""insertedjob"" AS (
	INSERT INTO ""hangfire"".""job"" (""invocationdata"", ""arguments"", ""createdat"")
	VALUES ('', '', now() at time zone 'utc') RETURNING ""id""
)
INSERT INTO ""hangfire"".""jobparameter"" (""jobid"", ""name"", ""value"")
SELECT ""insertedjob"".""id"", @name, @value
FROM ""insertedjob""
RETURNING ""jobid"";
";
            UseConnections((sql, connection) =>
            {
                var id = sql.Query<int>(
                    arrangeSql,
                    new { name = "name", value = "value" }).Single();

                var value = connection.GetJobParameter(Convert.ToString(id, CultureInfo.InvariantCulture), "name");

                Assert.Equal("value", value);
            });
        }

        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetFirstByLowestScoreFromSet(null, 0, 1));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ThrowsAnException_ToScoreIsLowerThanFromScore()
        {
            UseConnection(connection => Assert.Throws<ArgumentException>(
                () => connection.GetFirstByLowestScoreFromSet("key", 0, -1)));
        }

        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ReturnsNull_WhenTheKeyDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetFirstByLowestScoreFromSet(
                    "key", 0, 1);

                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ReturnsTheValueWithTheLowestScore()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""set"" (""key"", ""score"", ""value"")
values 
('key', 1.0, '1.0'),
('key', -1.0, '-1.0'),
('key', -5.0, '-5.0'),
('another-key', -2.0, '-2.0')";

            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql);

                var result = connection.GetFirstByLowestScoreFromSet("key", -1.0, 3.0);
                
                Assert.Equal("-1.0", result);
            });
        }

        [Fact, CleanDatabase]
        public void AnnounceServer_ThrowsAnException_WhenServerIdIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.AnnounceServer(null, new ServerContext()));

                Assert.Equal("serverId", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void AnnounceServer_ThrowsAnException_WhenContextIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.AnnounceServer("server", null));

                Assert.Equal("context", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void AnnounceServer_CreatesOrUpdatesARecord()
        {
            UseConnections((sql, connection) =>
            {
                var context1 = new ServerContext
                {
                    Queues = new[] { "critical", "default" },
                    WorkerCount = 4
                };
                connection.AnnounceServer("server", context1);

                var server = sql.Query(@"select * from ""hangfire"".""server""").Single();
                Assert.Equal("server", server.id);
                Assert.True(((string)server.data).StartsWith(
                    "{\"WorkerCount\":4,\"Queues\":[\"critical\",\"default\"],\"StartedAt\":"),
                    server.data);
                Assert.NotNull(server.lastheartbeat);

                var context2 = new ServerContext
                {
                    Queues = new[] { "default" },
                    WorkerCount = 1000 
                };
                connection.AnnounceServer("server", context2);
                var sameServer = sql.Query(@"select * from ""hangfire"".""server""").Single();
                Assert.Equal("server", sameServer.id);
                Assert.Contains("1000", sameServer.data);
            });
        }

        [Fact, CleanDatabase]
        public void RemoveServer_ThrowsAnException_WhenServerIdIsNull()
        {
            UseConnection(connection => Assert.Throws<ArgumentNullException>(
                () => connection.RemoveServer(null)));
        }

        [Fact, CleanDatabase]
        public void RemoveServer_RemovesAServerRecord()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""server"" (""id"", ""data"", ""lastheartbeat"")
values ('Server1', '', now() at time zone 'utc'),
('Server2', '', now() at time zone 'utc')";

            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql);

                connection.RemoveServer("Server1");

                var server = sql.Query(@"select * from ""hangfire"".""server""").Single();
                Assert.NotEqual("Server1", server.Id, StringComparer.OrdinalIgnoreCase);
            });
        }

        [Fact, CleanDatabase]
        public void Heartbeat_ThrowsAnException_WhenServerIdIsNull()
        {
            UseConnection(connection => Assert.Throws<ArgumentNullException>(
                () => connection.Heartbeat(null)));
        }

        [Fact, CleanDatabase]
        public void Heartbeat_UpdatesLastHeartbeat_OfTheServerWithGivenId()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""server"" (""id"", ""data"", ""lastheartbeat"")
values
('server1', '', '2012-12-12 12:12:12'),
('server2', '', '2012-12-12 12:12:12')";

            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql);

                connection.Heartbeat("server1");

                var servers = sql.Query(@"select * from ""hangfire"".""server""")
                    .ToDictionary(x => (string)x.id, x => (DateTime)x.lastheartbeat);

                Assert.NotEqual(2012, servers["server1"].Year);
                Assert.Equal(2012, servers["server2"].Year);
            });
        }

        [Fact, CleanDatabase]
        public void RemoveTimedOutServers_ThrowsAnException_WhenTimeOutIsNegative()
        {
            UseConnection(connection => Assert.Throws<ArgumentException>(
                () => connection.RemoveTimedOutServers(TimeSpan.FromMinutes(-5))));
        }

        [Fact, CleanDatabase]
        public void RemoveTimedOutServers_DoItsWorkPerfectly()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""server"" (""id"", ""data"", ""lastheartbeat"")
values (@id, '', @heartbeat)";

            UseConnections((sql, connection) =>
            {
                sql.Execute(
                    arrangeSql,
                    new[]
                    {
                        new { id = "server1", heartbeat = DateTime.UtcNow.AddDays(-1) },
                        new { id = "server2", heartbeat = DateTime.UtcNow.AddHours(-12) }
                    });

                connection.RemoveTimedOutServers(TimeSpan.FromHours(15));

                var liveServer = sql.Query(@"select * from ""hangfire"".""server""").Single();
                Assert.Equal("server2", liveServer.id);
            });
        }

        [Fact, CleanDatabase]
        public void GetAllItemsFromSet_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
                Assert.Throws<ArgumentNullException>(() => connection.GetAllItemsFromSet(null)));
        }

        [Fact, CleanDatabase]
        public void GetAllItemsFromSet_ReturnsEmptyCollection_WhenKeyDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetAllItemsFromSet("some-set");

                Assert.NotNull(result);
                Assert.Equal(0, result.Count);
            });
        }

        [Fact, CleanDatabase]
        public void GetAllItemsFromSet_ReturnsAllItems()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""set"" (""key"", ""score"", ""value"")
values (@key, 0.0, @value)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "some-set", value = "1" },
                    new { key = "some-set", value = "2" },
                    new { key = "another-set", value = "3" }
                });

                // Act
                var result = connection.GetAllItemsFromSet("some-set");

                // Assert
                Assert.Equal(2, result.Count);
                Assert.Contains("1", result);
                Assert.Contains("2", result);
            });
        }

        [Fact, CleanDatabase]
        public void SetRangeInHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetRangeInHash(null, new Dictionary<string, string>()));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void SetRangeInHash_ThrowsAnException_WhenKeyValuePairsArgumentIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetRangeInHash("some-hash", null));

                Assert.Equal("keyValuePairs", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void SetRangeInHash_MergesAllRecords()
        {
            UseConnections((sql, connection) =>
            {
                connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    { "Key1", "Value1" },
                    { "Key2", "Value2" }
                });

                var result = sql.Query(
                    @"select * from ""hangfire"".""hash"" where ""key"" = @key",
                    new { key = "some-hash" })
                    .ToDictionary(x => (string)x.field, x => (string)x.value);

                Assert.Equal("Value1", result["Key1"]);
                Assert.Equal("Value2", result["Key2"]);
            });
        }

        [Fact, CleanDatabase]
        public void GetAllEntriesFromHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
                Assert.Throws<ArgumentNullException>(() => connection.GetAllEntriesFromHash(null)));
        }

        [Fact, CleanDatabase]
        public void GetAllEntriesFromHash_ReturnsNull_IfHashDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetAllEntriesFromHash("some-hash");
                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetAllEntriesFromHash_ReturnsAllKeysAndTheirValues()
        {
            const string arrangeSql = @"
insert into ""hangfire"".""hash"" (""key"", ""field"", ""value"")
values (@key, @field, @value)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "some-hash", field = "Key1", value = "Value1" },
                    new { key = "some-hash", field = "Key2", value = "Value2" },
                    new { key = "another-hash", field = "Key3", value = "Value3" }
                });

                // Act
                var result = connection.GetAllEntriesFromHash("some-hash");

                // Assert
                Assert.NotNull(result);
                Assert.Equal(2, result.Count);
                Assert.Equal("Value1", result["Key1"]);
                Assert.Equal("Value2", result["Key2"]);
            });
        }

        private void UseConnections(Action<NpgsqlConnection, PostgreSqlConnection> action)
        {
            using (var sqlConnection = ConnectionUtils.CreateConnection())
            using (var connection = new PostgreSqlConnection(sqlConnection, _providers))
            {
                action(sqlConnection, connection);
            }
        }

        private void UseConnection(Action<PostgreSqlConnection> action)
        {
            using (var connection = new PostgreSqlConnection( 
                ConnectionUtils.CreateConnection(),
                _providers))
            {
                action(connection);
            }
        }

        public static void SampleMethod(string arg){ }
    }
}
