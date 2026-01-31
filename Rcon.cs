using EmuWarface.Core;
using EmuWarface.Game;
using EmuWarface.Game.GameRooms;
using EmuWarface.Game.Items;
using EmuWarface.Game.Requests;
using MySql.Data.MySqlClient;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace EmuWarface
{
    internal class Rcon
    {
        internal static HttpListener Http = new HttpListener();
        static HttpListenerContext Client;

        internal async static void Init()
        {
            Console.WriteLine("[Rcon] Initialization.");
            Http.Prefixes.Add($"http://*:8000/api/");
            Http.Start();
            Console.WriteLine("[Rcon] Initialization. Success");
            await Task.Run(() => Server());
        }

        private async static void Send(object message)
        {
            try
            {
                await Client.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(message.ToString()), 0, Encoding.UTF8.GetBytes(message.ToString()).Length);
                Client.Response.Close();
            }
            catch { }
        }
        public static string Token = "viXWKHNXu04ZHLSdv6a0rhMB3EnfAfVhXJBJ6HB";

        private static async void Server()
        {
            Console.WriteLine("[RconServer] Initialization.");
            while (true)
            {
                try
                {
                    Client = await Http.GetContextAsync();
                    string clientIP = Client.Request.RemoteEndPoint.ToString().Split(':')[0];
                    var queryString = Client.Request.QueryString;
                    var RawUrl = Client.Request.RawUrl;

                    var request = Client.Request;
                    var response = Client.Response;

                    if (request.HttpMethod == "POST" &&
                        request.Url.AbsolutePath.Equals("/api/upload", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileName = request.QueryString["name"] ?? "upload.bin";
                        fileName = Path.GetFileName(fileName);

                        Directory.CreateDirectory("uploads");
                        var outPath = Path.Combine("uploads", fileName);

                        using (var fs = File.Create(outPath))
                        {
                            await request.InputStream.CopyToAsync(fs);
                        }

                        var ok = System.Text.Encoding.UTF8.GetBytes("OK");
                        response.StatusCode = 200;
                        await response.OutputStream.WriteAsync(ok, 0, ok.Length);
                        response.Close();
                        continue;
                    }
                    if (request.HttpMethod == "GET")
                    {
                        switch (queryString["method"])
                        {
                            case "set_promocde_with_vpn":
                                string guid = "ATLAS-" + Guid.NewGuid().ToString();
                                var SQLCMD = new MySqlCommand("INSERT INTO pincodes_create SET name=@name, items=@items, amount_pin=1");
                                SQLCMD.Parameters.AddWithValue("@name", guid);
                                Int64 v = Int64.Parse(queryString["isFreeLicense"]);
                                var itemsXml = "";
                                if (v == 1)
                                {
                                    itemsXml =
    "<items>\r\n" +
    "\r\n" +
    "    <item id='cry_money' image='https://wf.cdn.gmru.net/wiki/images/1/1e/Cry_money_icon.png' time_code='50000' time_text='Количество (50000)' label='Кредиты' type='money'/>\r\n" +
    "\r\n" +
    "</items>";
                                }
                                else
                                {
                                    itemsXml =
      "<items>\r\n" +
      "\r\n" +
      "    <item id='ar62_gold01_shop' image='https://cdn.wfts.su/weapons/weapons_ar62_gold01.png' time_code='0' time_text='Навсегда' label='Золотой А-545' type='weapon'/>\r\n" +
      "    <item id='shg74_gold01_shop' image='https://cdn.wfts.su/weapons/weapons_shg74_gold01.png' time_code='0' time_text='Навсегда' label='Золотой Winchester SXP' type='weapon'/>\r\n" +
      "    <item id='smg38_gold01_shop' image='https://cdn.wfts.su/weapons/weapons_smg38_gold01.png' time_code='0' time_text='Навсегда' label='Золотой CZ Scorpion EVO 3 A1' type='weapon'/>\r\n" +
      "    <item id='sr68_gold01_shop' image='https://cdn.wfts.su/weapons/weapons_sr68_gold01.png' time_code='0' time_text='Навсегда' label='Золотой PGM Ultima Ratio' type='weapon'/>\r\n" +
      "    <item id='kn67_mvt25spring04' image='https://cdn.wfts.su/weapons/weapons_kn67_mvt25spring04.png' time_code='0' time_text='Навсегда' label='Катана Наследие' type='weapon'/>\r\n" +
      "\r\n" +
      "</items>";
                                }

                                SQLCMD.Parameters.AddWithValue("@items", itemsXml);

                                SQL.Query(SQLCMD);
                                Send(guid);

                                break;
                            case "remote_client":
                                var data = "Remote client OK";
                                var bytes = System.Text.Encoding.UTF8.GetBytes(data);
                                response.StatusCode = 200;
                                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                                break;

                        }
                        if (queryString["token"] != null && queryString["token"] == Token)
                        {
                            Profile ProfileByNick = null;
                            if (queryString["nickname"] != null) ProfileByNick = Profile.GetProfileForNickname(queryString["nickname"]);
                            switch (queryString["method"])
                            {
                                case "give":
                                    switch (queryString["category"])
                                    {
                                        case "weapons":
                                            if (queryString["items"] != null && queryString["nickname"] != null && queryString["type"] != null && queryString["isDev"] != null)
                                            {
                                                switch (queryString["type"])
                                                {
                                                    case "Permanent":
                                                        API.GiveItem(UserStatus.Administrator, 6530883040, ProfileByNick.Nickname, queryString["items"], ItemType.Permanent);
                                                        break;
                                                    case "Expiration":
                                                        API.GiveItem(UserStatus.Administrator, 6530883040, ProfileByNick.Nickname, queryString["items"], ItemType.Expiration, EmuExtensions.ParseSeconds(queryString["time"]));
                                                        break;
                                                    case "Consumable":
                                                        API.GiveItem(UserStatus.Administrator, 6530883040, ProfileByNick.Nickname, queryString["items"], ItemType.Consumable, 0, quantity: int.Parse(queryString["amount"]));
                                                        break;
                                                }
                                                Send("Successfully!");
                                            }
                                            else
                                            {
                                                Send("ERROR");
                                            }
                                            break;
                                        case "money":
                                            if (queryString["nickname"] != null && queryString["currency"] != null & queryString["amount"] != null)
                                            {
                                                API.GiveMoney(UserStatus.Administrator, 6530883040, ProfileByNick.Nickname, queryString["currency"], int.Parse(queryString["amount"]));
                                            }
                                            else Send("Error!");
                                            break;
                                        case "achiev":
                                            if (queryString["nickname"] != null && queryString["achievs"] != null)
                                            {
                                                API.GiveAchiev(UserStatus.Administrator, 6530883040, ProfileByNick.Nickname, uint.Parse(queryString["achievs"]));
                                            }
                                                break;
                                        default:
                                            Send("ERROR");
                                            break;
                                    }
                                    break;
                                case "remotescreen":

                                    if (queryString["nickname"] != null)
                                    {

                                        var screenClient = EmuWarface.Server.Clients.FirstOrDefault(x => x.Profile?.Nickname == queryString["nickname"]);
                                        XElement remote_screenshot = new XElement("remote_screenshot");
                                        XmlDocument xmlDocument = new XmlDocument();
                                        xmlDocument.LoadXml(remote_screenshot.ToString());
                                        XmlElement xmlElement = xmlDocument.DocumentElement;
                                        screenClient.Send(xmlElement);
                                        Send("Successfully!");
                                    }
                                    else Send("Error!");
                                    break;
                            }
                        }
                        Send("ERROR");
                    }
                }
                catch
                {
                    Send("Error!");
                }
            }
        }
    }
}
