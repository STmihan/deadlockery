using DeadlockAPI;
using GenHTTP.Engine;
using GenHTTP.Modules.Functional;
using SteamKit2.GC.Deadlock.Internal;

namespace DeadlockMatchInfoServer
{
    internal class Program
    {
        public static DeadlockClient client;

        public static void Main(string[] args)
        {
            string? username = Environment.GetEnvironmentVariable("DEADLOCK_STEAMWORKS_USERNAME");
            string? password = Environment.GetEnvironmentVariable("DEADLOCK_STEAMWORKS_PASSWORD");
            string? isProdStr = Environment.GetEnvironmentVariable("DEADLOCK_STEAMWORKS_PROD");
            bool isProd = isProdStr == "true";

            if (username == null || password == null)
            {
                Console.WriteLine("Missing login");
                return;
            }

            client = new DeadlockClient(username, password);

            Task.Run(() =>
            {
                var service = Inline.Create()
                .Get("/match/:matchId", async (uint matchId) =>
                {
                    if (!client.IsConnected)
                    {
                        return "{\"result\":\"bad\"}";
                    }
                    var e = await client.GetMatchMetaData(matchId);
                    if (e == null)
                    {
                        return "{\"result\":\"bad\"}";
                    }
                    if (e.Data.result != CMsgClientToGCGetMatchMetaDataResponse.EResult.k_eResult_Success)
                    {
                        return "{\"result\":\"bad\"}";
                    }
                    return $"{{\"result\":\"ok\",\"metadata\":\"http://replay{e.Data.cluster_id}.valve.net/1422450/{matchId}_{e.Data.metadata_salt}.meta.bz2\"}}";
                });
                Host.Create().Handler(service).Development(!isProd).Console().Port(9900).Run();
            });

            client.Connect();
            client.Wait();
        }
    }
}
