using EmuWarface.Core;
using EmuWarface.Game;
using EmuWarface.Game.Clans;
using EmuWarface.Game.Enums;
using EmuWarface.Game.GameRooms;
using EmuWarface.Game.Missions;
using EmuWarface.Game.Requests;
using EmuWarface.Xmpp;
using Google.Protobuf.WellKnownTypes;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using XAct;

namespace EmuWarface
{
    public static class Server
    {

        public static Dictionary<string, long> LastTimeConnect = new Dictionary<string, long>();
        public static Dictionary<string, long> TempBan = new Dictionary<string, long>();


        private static Socket _socket = new Socket(AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);

        public static X509Certificate2 Certificate = new X509Certificate2("Config/server.pfx", EmuConfig.Settings.CertSecret);

        public static List<Invitation>  Invitations { get; set; } = new List<Invitation>();
        public static List<Client>      Clients     { get; set; } = new List<Client>();

        public static List<MasterServer> Channels = new List<MasterServer>();
        public static List<Client> Dedicateds
        {
            get
            {
                lock (Clients)
                {
                    return Clients.Where(x => x.IsDedicated).ToList();
                }
            }
        }

        public static Dictionary<Client, DateTime> AntiCheatPing = new Dictionary<Client, DateTime>();

        public static uint DedicatedSeed;

        public static void Init()
        {
            InitChannels();
            _socket.Bind(new IPEndPoint(IPAddress.Any, EmuConfig.Settings.Port));
            _socket.Listen(10);
            Log.Info($"Server started on {EmuConfig.Settings.Port} port");
            _socket.BeginAccept(OnAccept, null);
        }

        public static void InitChannels()
        {
            foreach (var mscfg in EmuConfig.MasterServers)
            {
                Channels.Add(new MasterServer(mscfg.ServerId, mscfg.Resource, mscfg.Channel, mscfg.RankGroup, mscfg.MinRank, mscfg.MaxRank, mscfg.Bootstrap));
            }
        }

        private static void OnAccept(IAsyncResult Result)
        {
            _socket.BeginAccept(OnAccept, null);
            try
            {
                var cSocket = _socket.EndAccept(Result);
                var ip      = ((IPEndPoint)cSocket.RemoteEndPoint).Address.ToString();

                //ТУТ Я ВЪЕБАЛ В ОНИКСА TODO 

                long curTimeConnect = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (!EmuConfig.Settings.DedicatedHosts.Contains(ip))
                {
                    if (TempBan.ContainsKey(ip) && TempBan[ip] > curTimeConnect)
                    {
                        cSocket.Dispose();
                        return;
                    }

                    //if (LastTimeConnect.ContainsKey(ip) && curTimeConnect - LastTimeConnect[ip] < 50)
                    //{
                    //    try
                    //    {
                    //        File.AppendAllText("BANTCP" + DateTime.Now.ToString("yyyy-MM-dd HH") + ".txt", ip + "\n");
                    //    }
                    //    catch
                    //    {
                    //
                    //    }
                    //    TempBan[ip] = curTimeConnect + 300;
                    //    cSocket.Dispose();
                    //    return;
                    //}

                    LastTimeConnect[ip] = curTimeConnect;
                }

                Client client = new Client(cSocket);

                if (!client.timerInitializated && !client.IsDedicated)
                {

                    System.Timers.Timer antiCheatTimer = new System.Timers.Timer(30_000);
                    antiCheatTimer.Elapsed += (s, e) =>
                    {
                        var now = DateTime.UtcNow;

                        foreach (var kv in Server.AntiCheatPing.ToList())
                        {
                            if (now - kv.Value > TimeSpan.FromMinutes(7))
                            {
                                client.Dispose();
                                Server.AntiCheatPing.Remove(kv.Key);
                            }
                        }
                    };
                    client.timerInitializated = true;
                    antiCheatTimer.Start();
                }

                Log.Info(client.IPAddress + " connected");
                Task.Factory.StartNew(() => ReadXmlStream(client), TaskCreationOptions.LongRunning);
            }
            catch (Exception e)
            {
                Log.Error(e.ToString());
                API.SendAdmins(e.ToString());
            }

            GC.Collect(0);
        }

        private static void ReadXmlStream(Client client)
        {
            bool done           = false;
            Exception exception = null;

            try
            {
                do
                {
                    string data = string.Empty;
                    exception   = null;

                    

                    try
                    {
                        data = client.Read();
                    }
                    catch (ServerException e)
                    {
                        Log.Warn("[Server] {0} (ip: {1})", e.Message, client.IPAddress);
                        break;
                    }
                    catch (IOException e)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Log.Error(e.ToString());
                        break;
                    }

                    if (string.IsNullOrEmpty(data))
                        break;

                    if (EmuConfig.Settings.XmppDebug)
                        Log.Xmpp(data);

                    if (data == "protect_init")
                        continue;

                    if (data == "</stream:stream>")
                        break;

                    if (!data.StartsWith("<"))
                        break;

                    if (new Regex("<stream:stream([\\s\\S]+?)>").Matches(data).Count > 0)
                    {
                        client.StreamFeatures();
                        continue;
                    }

                    XmlElement elem = Xml.Parse(data);
                    if (elem.LocalName == "starttls"
                        && elem.NamespaceURI == "urn:ietf:params:xml:ns:xmpp-tls")
                    {
                        client.StartTls();
                        continue;
                    }
                    if (elem.LocalName == "auth"
                        && elem.NamespaceURI == "urn:ietf:params:xml:ns:xmpp-sasl")
                    {
                        if (!client.Authenticate(elem))
                            break;

                        client.State = ConnectionState.Authed;
                        continue;
                    }
                    if (elem.LocalName == "achiev_ends")
                    {
                        try
                        {
                           // List<string> DictionaryProgresses = new List<string>();
                           // Int64 progress = 0;
                           // string basePath = AppDomain.CurrentDomain.BaseDirectory;
                           // string achievPath = Path.Combine(basePath, "Config", "achievements");
                           //
                           //
                           //
                           // foreach (XElement xElement in client.Achievmenents)
                           // {
                           //     var weaponAttr = xElement?.Attribute("weapon");
                           //     if (weaponAttr == null)
                           //         continue;
                           //     
                           //     string result = xElement?.Attribute("weapon")?.Value.Split('_')[0];
                           //
                           //     Int64 id_achiev = 0;
                           //     
                           //     foreach (string filePath in Directory.GetFiles(achievPath, "*",SearchOption.AllDirectories))
                           //     {
                           //         XElement xElement1 = XElement.Parse(File.ReadAllText(filePath));
                           //         Console.WriteLine(xElement1);
                           //         if (xElement1.Element("UI").Attribute("name").Value.Contains(result))
                           //         {
                           //             progress++;
                           //             id_achiev = Int64.Parse(xElement1?.Attribute("id")?.Value);
                           //             break;
                           //         }
                           //     }
                           //     if (id_achiev != 0)
                           //     {
                           //        
                           //     }
                           //     DictionaryProgresses.AddRange(new[] { xElement.Attribute("profile_id").Value, id_achiev.ToString(), progress.ToString() });
                           // }
                           // foreach (string item in DictionaryProgresses)
                           // {
                           //     Int64 id_achiev = item[0];
                           //     Int64 profile_idClient = item.Key;
                           //
                           //     Client client1 = Server.Clients.Find(e => e.ProfileId == profile_idClient);
                           //     if (client1 == null) continue;
                           //
                           //     var cmd = new MySqlCommand("SELECT * FROM achievements WHERE profile_id=@profile_id AND achievement_id=@achievement_id");
                           //     cmd.Parameters.AddWithValue("@profile_id", client1.Profile.Id);
                           //     cmd.Parameters.AddWithValue("@achievement_id", id_achiev);
                           //
                           //     var Read = SQL.QueryRead(cmd);
                           //     if (Read.Rows.Count == 0)
                           //     {
                           //         var cmd1 = new MySqlCommand("INSERT INTO achievements SET achievement_id=@achievement_id WHERE profile_id=@profile_id");
                           //         cmd1.Parameters.AddWithValue("@achievement_id", id_achiev);
                           //         cmd1.Parameters.AddWithValue("@profile_id", client1.Profile.Id);
                           //         SQL.Query(cmd1);
                           //     }
                           //     else
                           //     {
                           //         var cmd2 = new MySqlCommand("UPDATE achievements SET achievement_id=@achievement_id WHERE profile_id=@profile_id");
                           //         cmd2.Parameters.AddWithValue("@achievement_id", id_achiev);
                           //         cmd2.Parameters.AddWithValue("@profile_id", client1.Profile.Id);
                           //         SQL.Query(cmd2);
                           //     }
                           //     Console.WriteLine($"[telemetry] user [{client1.Profile.Id}] setted achievemt_id [{id_achiev}]!");
                           //
                           //     client1?.ResyncProfie();
                           //
                           // }
                           // client?.Achievmenents?.Clear();
                           // client?.AchievmenentsProgress?.Clear();
                           // DictionaryProgresses?.Clear();
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.ToString());
                        }
                    }
                    if (elem.LocalName == "update_achievement_weapons")
                    {
                        //XElement xElement = XElement.Parse(data);
                        //client.Achievmenents.Add(xElement);
                    }
                    if (elem.LocalName == "anticheat_ping") {
                        if (client.Profile != null)
                        {
                            if (!AntiCheatPing.ContainsKey(client))
                                AntiCheatPing[client] = DateTime.UtcNow;
                        }


                    }
                    if (elem.LocalName == "anticheat_log")
                    {
                        XElement xElement = XElement.Parse(elem?.ToString());
                        if (xElement != null)
                        {
                            var dll_injected = xElement.Attribute("dll_name").Value;
                            if (client.Profile != null)
                            {
                                Int64 profile_id = client.Profile.Id;
                                MySqlCommand mySqlCommand = new MySqlCommand("INSERT INTO anticheat_log SET profile_id=@profile_id, nickname=@nickname, dll_name=@dll_name");
                                mySqlCommand.Parameters.AddWithValue("@profile_id", profile_id);
                                mySqlCommand.Parameters.AddWithValue("@nickname", client.Profile.Nickname);
                                mySqlCommand.Parameters.AddWithValue("@dll_name", dll_injected);
                                SQL.Query(mySqlCommand);
                            }
                            
                            

                        }
                    }


                    switch (elem.Name)
                    {
                        case "iq":
                            {
                                Iq iq = new Iq(elem);

                                switch (elem.FirstChild.Name)
                                {
                                    case "bind":
                                        {
                                            if (elem["bind"].NamespaceURI != "urn:ietf:params:xml:ns:xmpp-bind" || client.State != ConnectionState.Authed)
                                                break;

                                            

                                            client.Bind(iq, data);
                                        }
                                        break;
                                    case "session":
                                        {
                                            if (elem["session"].NamespaceURI != "urn:ietf:params:xml:ns:xmpp-session" || client.State != ConnectionState.Binded)
                                                break;

                                            client.IqResult(iq);
                                        }
                                        break;
                                    case "query":
                                        {
                                            if (elem["query"].NamespaceURI != "urn:cryonline:k01" || client.State != ConnectionState.Binded)
                                                break;

                                            try
                                            {
                                                if (iq.Query.LocalName == "data")
                                                    iq.Uncompress();

                                                if (iq.To.Resource == "GameClient")
                                                {
                                                    Client target = null;

                                                    lock (Server.Clients)
                                                    {
                                                        target = Server.Clients.FirstOrDefault(x => x.Jid == iq.To);
                                                    }

                                                    if (target != null && target.Jid != client.Jid)
                                                    {
                                                        target.Send(iq);
                                                    }
                                                    else
                                                    {
                                                        client.QueryResult(iq.SetError(1));
                                                    }
                                                    continue;
                                                }
                                                var method = QueryBinder.Handler.FirstOrDefault(x => x.QueryNames.Contains(iq.Query.LocalName) && x.QueryType == iq.Type)?.Method;

                                                if (method == null)
                                                {
                                                    if (iq.Type == IqType.Result)
                                                        continue;
                                                    API.SendAdmins(string.Format("Custom query '{0}' not found\n{1}", iq.Query.LocalName, iq.ToString().Replace("><", ">\n<")));
                                                    Log.Warn(string.Format("[Server] Custom query '{0}' not found (userd_id: {1}, ip:{2})", iq.Query.LocalName, client.UserId, client.IPAddress));
                                                    //client.IqResult(iq.SetError(1));
                                                }
                                                else
                                                {
                                                    method.Invoke(null, new object[] { client, iq });
                                                }
                                            }
                                            catch (TargetInvocationException tie)
                                            {
                                                exception = tie.InnerException;
                                            }
                                            catch (Exception ex)
                                            {
                                                exception = ex;
                                            }

                                            if (exception != null)
                                            {
                                                if (exception is QueryException ex)
                                                {
                                                    client.IqResult(iq.SetError(ex.CustomCode));
                                                }
                                                else if (exception is ServerException srvEx)
                                                {
                                                    client.IqResult(iq.SetError(1));
                                                    Log.Warn("[ServerException] {0} (user_id: {1}, ip: {2})", srvEx.Message, client.UserId, client.IPAddress);
                                                }
                                                else
                                                {
                                                    string err = string.Format("[{0}] user_id: {1}, ip: {2}\n{3}", exception.GetType().Name, client.UserId, client.IPAddress, exception.ToString());

                                                    Log.Error(err + "\n" + data);
                                                    API.SendAdmins(err);
                                                    //done = true;  
                                                }
                                            }
                                        }
                                        break;
                                }
                            }
                            break;
                        case "message":
                            {
                                try
                                {
                                    if (client.Profile == null)
                                        continue;

                                    var to = new Jid(elem.GetAttribute("to"));
                                    var msg = elem.FirstChild.InnerText;
                                    if (msg.StartsWith("/"))
                                    {
                                        string[] array = msg.Split(' ');
                                        switch (array[0])
                                        {
                                            case "/kick":
                                                if(client.Profile.Room != null && client.Profile.Id == 108 || client.Profile.Id == 143)
                                                {
                                                    GameRoom room = client.Profile.Room;
                                                    Client client1;
                                                    lock (Server.Clients)
                                                    {
                                                        client1 = Server.Clients.FirstOrDefault(x => x.Profile?.Nickname == array[1]);
                                                    }
                                                    if (client1 != null)
                                                    {
                                                        if(client != client1)
                                                        {
                                                            ExecCommand.Command(room.GetReadyDedicated(), null, "kickpid " + client1.Profile.Id);
                                                            room.KickPlayer(client1, RoomPlayerRemoveReason.KickMaster);
                                                        }
                                                    }
                                                }
                                                break;
                                            case "/change":
                                                if (client.Profile.Room != null && client.Profile.Id == 108 || client.Profile.Id == 143)
                                                {
                                                    if (GameData.PvPMissions.FirstOrDefault(x => x.Name.Contains(array[1])) != null)
                                                    {
                                                        Mission mission = Mission.GetMission(client.Profile.Room.Type, GameData.PvPMissions.FirstOrDefault(x => x.Name.Contains(array[1])).Uid);
                                                        client.Profile.Room.SetMission(mission);
                                                        client.Profile.Room.Update();
                                                    }
                                                }
                                            break;
                                        }
                                    }
                                    var unmute_time = Profile.GetMuteTime(client.UserId);
                                    if (unmute_time != -1)
                                        continue;

                                    Log.Chat("[{0}] {1}({2}): {3}", to.ToString(), client.Profile.Nickname, client.UserId, msg);

                                    if (!GameData.ValidateInputString("ChatText", msg))
                                    {
                                        API.Mute(client.Profile.Nickname, "3.1", "1h");
                                        continue;
                                    }

                                    //TODO валидировать куда он шлет сообщение

                                    elem.Attr("from", new Jid(to.Domain, to.Node, client.Profile.Nickname));

                                    if (to.Node.Contains("global"))
                                    {
                                        lock (Server.Clients)
                                        {
                                            Server.Clients.Where(x => x.Channel == client.Channel).ToList().ForEach(t => t.Send(elem));
                                        }
                                    }
                                    else if (to.Node.Contains("room"))
                                    {
                                        var rCore = client.Profile.Room?.GetExtension<GameRoomCore>();

                                        if (rCore == null)
                                            continue;

                                        lock (rCore.Players)
                                        {
                                            rCore.Players.ForEach(t => t.Send(elem));
                                        }
                                    }
                                    else if (to.Node.Contains("clan"))
                                    {
                                        lock (Server.Clients)
                                        {
                                            Server.Clients.Where(x => x.Profile?.ClanId == client.Profile.ClanId).ToList().ForEach(t => t.Send(elem));
                                        }
                                    }
                                    else
                                    {
                                        elem.Attr("type", "error").Attr("from", to.ToString()).Attr("to", client.Jid);

                                        XmlElement error = Xml.Element("error").Attr("type", "modify").Attr("code", "406");
                                        error.Child(Xml.Element("not-acceptable", "urn:ietf:params:xml:ns:xmpp-stanzas"));
                                        error.Child(Xml.Element("text", "urn:ietf:params:xml:ns:xmpp-stanzas").Text("Only occupants are allowed to send messages to the conference"));

                                        elem.Child(error);
                                        client.Send(elem);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    exception = ex;
                                    done = true;
                                }
                            }
                            break;
                    }
                }
                while (client.IsConnected && !done);
            }
            catch (IOException) { }
            finally
            {
                if(done && exception != null)
                {
                    string err = string.Format("[ServerException] user_id: {0}, ip: {1}\n{2}", client.UserId, client.IPAddress, exception.ToString());

                    Log.Error(err);
                    API.SendAdmins(err);
                }

                client.Dispose();
                client = null;
            }
        }
    }
}
