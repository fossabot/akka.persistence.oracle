using System;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence.Query;
using Akka.Persistence.Query.Sql;
using Akka.Streams;
using Akka.Streams.Dsl;

namespace Benchmark
{
    internal static class Program
    {
        // if you want to benchmark your persistent storage provides, paste the configuration in string below
        // by default we're checking against in-memory journal
        private static readonly Config Config = ConfigurationFactory.ParseString(@"
            akka {
                loglevel = INFO
                suppress-json-serializer-warning = true
                persistence.journal {
                    plugin = ""akka.persistence.journal.oracle""
                    oracle {
                        class = ""Akka.Persistence.Oracle.Journal.BatchingOracleJournal, Akka.Persistence.Oracle""
                        plugin-dispatcher = ""akka.actor.default-dispatcher""
                        table-name = INV_EVENTJOURNAL
                        schema-name = AKKA_PERSISTENCE_TEST
                        auto-initialize = on
                        connection-string-name = ""TestDb""
			            connection-timeout = 30s
                        refresh-interval = 1s
                        max-concurrent-operations = 64
                        max-batch-size = 100
                        circuit-breaker {
                            max-failures = 3
                            call-timeout = 10s
                            reset-timeout = 10s
                        }
                    }
                }
            }");

        public const int ActorCount = 1;
        public const int MessagesPerActor = 10;

        private static void Main(string[] args)
        {
            var system = ActorSystem.Create("persistent-benchmark", Config.WithFallback(ConfigurationFactory.Default()));

            var mat = ActorMaterializer.Create(system);
            var backoffSource = RestartSource.WithBackoff(() =>
                {
                    Console.WriteLine("Starting stream...");

                    return PersistenceQuery.Get(system)
                        .ReadJournalFor<SqlReadJournal>(SqlReadJournal.Identifier)
                        .EventsByPersistenceId("a-1", 0L, long.MaxValue)
                        .Throttle(3, TimeSpan.FromSeconds(3), 3, ThrottleMode.Shaping)
                        .Log("Log", e =>
                        {
                            Console.WriteLine(e.SequenceNr);
                            return e;
                        });
                },
                minBackoff: TimeSpan.FromSeconds(10),
                maxBackoff: TimeSpan.FromSeconds(30),
                randomFactor: 0.2);

            backoffSource.RunWith(Sink.Ignore<EventEnvelope>(), mat);

            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();

            system.Terminate();
        }
    }
}
