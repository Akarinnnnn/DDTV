﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThirdParty.Json.LitJson;

namespace DDTV_Core.SystemAssembly.BilibiliModule.API.LiveChatScript
{
    public class LiveChatListener
    {
        private ClientWebSocket m_client;

        public event EventHandler<MessageEventArgs> MessageReceived;
        public event EventHandler<EventArgs> DisposeSent;

        public byte[] m_ReceiveBuffer;
        public DanMu.DanMuClass.DanMuWssInfo host { set; get; }
        public CancellationTokenSource m_innerRts;
        public int TroomId = 0;
        public bool startIn = false;
        public string wss_S = "";
        public long mid = 0;
        public bool IsUserDispose=false;

        public LiveChatListener()
        {
            
        }

        public async void Connect(int roomId,long uid)
        {
            try
            {
                m_ReceiveBuffer = new byte[8192 * 1024];
                TroomId = roomId;
                startIn = true;
                mid= uid;
                await ConnectAsync(roomId, null);
            }
            catch (Exception e)
            {
                Log.Log.AddLog(nameof(LiveChatListener), Log.LogClass.LogType.Warn, $"LiveChatListener初始化Connect出现错误", true, e);
                Dispose();
            }
        }

        public async Task ConnectAsync(int roomId, CancellationToken? cancellationToken = null)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("");
            }
            m_client = new ClientWebSocket();
            m_innerRts = new CancellationTokenSource();
            JObject JO = new JObject();
            try
            {
                await m_client.ConnectAsync(new Uri("wss://"+host.host_list[0].host+"/sub"), cancellationToken ?? new CancellationTokenSource(30000).Token);

                //await m_client.ConnectAsync(new Uri("wss://broadcastlv.chat.bilibili.com/sub"), cancellationToken ?? new CancellationTokenSource(300000).Token);
            }
            catch (Exception e)
            {
                Log.Log.AddLog(nameof(LiveChatListener), Log.LogClass.LogType.Warn, $"WSS连接发生错误", true, e,false);
                Dispose();
                //Console.WriteLine(e.ToString());
            }

            int realRoomId = roomId;//await _getRealRoomId(roomId);

            await _sendObject(7, new
            {
                uid = 0,//string.IsNullOrEmpty(BilibiliModule.User.BilibiliUser.account.uid)?0:long.Parse(User.BilibiliUser.account.uid),
                roomid = realRoomId,
                protover = 2,
                platform = "web",
                clientver = "1.14.0",
                type = "1",
                key = host.token
            });

            _ = _innerLoop().ContinueWith((t) =>
           {
               if (t.IsFaulted)
               {
                   //UnityEngine.Debug.LogError(t.Exception.);
                   if (!m_innerRts.IsCancellationRequested)
                   {
                       MessageReceived(this, new ExceptionEventArgs(t.Exception.InnerException));
                       m_innerRts.Cancel();
                   }
               }
               else
               {
                   //POST-CANCEL
                   //InfoLog.InfoPrintf("LiveChatListener连接断开，房间号:"+ realRoomId, InfoLog.InfoClass.Debug);
                   Console.WriteLine("LiveChatListender cancelled.");

               }
               try
               {
                   m_client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).Wait();
               }
               catch (Exception)
               {
                   Dispose();
               }
           });
            _=_innerHeartbeat();
        }

        public void Close()
        {
            m_ReceiveBuffer = null;

            try
            {
                if (m_innerRts!=null)
                {
                    m_innerRts.Cancel();
                }

            }
            catch (Exception)
            { }
            try
            {
                m_client.Dispose();
                //InfoLog.InfoPrintf($"{TroomId}房间LCL连接已断开，ClientWebSocket对象回收完成:", InfoLog.InfoClass.Debug);
                //Console.WriteLine($"{TroomId}房间LCL连接已断开，ClientWebSocket对象回收完成");
            }
            catch (Exception)
            {
                //InfoLog.InfoPrintf($"×{TroomId}房间LCL连接已断开，ClientWebSocket对象回收失败:", InfoLog.InfoClass.Debug);
                //Console.WriteLine($"{TroomId}房间LCL连接已断开，ClientWebSocket对象回收失败");
            }
            try
            {
                m_innerRts.Dispose();
                //InfoLog.InfoPrintf($"{TroomId}房间LCL连接已断开，CancellationTokenSource对象回收完成:", InfoLog.InfoClass.Debug);
            }
            catch (Exception)
            {
                //InfoLog.InfoPrintf($"×{TroomId}房间LCL连接已断开，CancellationTokenSource对象回收失败:", InfoLog.InfoClass.Debug);
            }
            try
            {
                m_ReceiveBuffer = null;
            }
            catch (Exception)
            {

                //throw;
            }
        }

        private bool _disposed = false;

        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
            }
            if (DisposeSent != null)
                DisposeSent.Invoke(this, EventArgs.Empty);
            _disposed = true;
        }

        private async Task _innerLoop()
        {
#if DEBUG
            //InfoLog.InfoPrintf("LiveChatListener开始连接，房间号:" + TroomId, InfoLog.InfoClass.Debug);
            Console.WriteLine("LiveChatListender start.");
#endif
            while (!m_innerRts.IsCancellationRequested)
            {
                try
                {
                    WebSocketReceiveResult result;
                    int length = 0;
                    do
                    {
                        try
                        {
                            result = await m_client.ReceiveAsync(
                          new ArraySegment<byte>(m_ReceiveBuffer, length, m_ReceiveBuffer.Length - length),
                          m_innerRts.Token);
                            length += result.Count;
                        }
                        catch (Exception e)
                        {
                            throw;
                        }
                    }
                    while (!result.EndOfMessage);
                    DepackDanmakuData(m_ReceiveBuffer);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    continue;
                }
                catch (WebSocketException we)
                {
                    throw we;
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    continue;
                }
                catch (Exception e)
                {
                    //UnityEngine.Debug.LogException(e);
                    throw e;
                }
            }
        }

        private IEnumerable<ArraySegment<byte>> _depack(ArraySegment<byte> array)
        {
            int ioffset = 0;
            while (ioffset < array.Count)
            {
                int totalPackageCount = BEBitConverter.ToInt32(array.Array, array.Offset + ioffset);
                ushort protocol = BEBitConverter.ToUInt16(array.Array, array.Offset + ioffset + 6);
                int type = BEBitConverter.ToInt32(array.Array, array.Offset + ioffset + 8);
                if (type == 5)
                {
                    ushort headerCount = BEBitConverter.ToUInt16(m_ReceiveBuffer, array.Offset + ioffset + 4);
                    ArraySegment<byte> packageSegment = new ArraySegment<byte>(
                        array.Array,
                        ioffset + headerCount, totalPackageCount - headerCount);
                    if (protocol == 0)
                    {
                        yield return packageSegment;
                    }
                    else if (protocol == 2)
                    {
                        throw new NotSupportedException("Gzip not supported yet.");
                        //_depack(segment.)
                        //foreach (var i in _depack(packageSegment)) yield return i;
                    }
                }
                ioffset += totalPackageCount;
            }
        }

        private void _parse(string jsonBody)
        {
            var obj = new JObject();
            try
            {        
                jsonBody = ReplaceString(jsonBody);
                if (jsonBody.Contains("DANMU_MSG"))
                {
                    jsonBody = jsonBody.Replace("extra\":\"{\"send_from_me", "extra\":{\"send_from_me");
                    jsonBody=jsonBody.Replace("}\"}","}}");
                }
                obj = JObject.Parse(jsonBody); ///JsonMapper.ToObject(jsonBody);
            }
            catch (Exception) { return; }
            string cmd = (string)obj["cmd"];
            if(cmd.Contains("DANMU_MSG"))
            {
                MessageReceived(this, new DanmuMessageEventArgs(obj));
            }
            else if (cmd.Contains("SUPER_CHAT_MESSAGE"))
            {
                MessageReceived(this, new SuperchatEventArg(obj));
            }
            else
            switch (cmd)
            {
                //弹幕信息
                case "DANMU_MSG":
                    MessageReceived(this, new DanmuMessageEventArgs(obj));
                    break;
                //SC信息
                case "SUPER_CHAT_MESSAGE":
                    MessageReceived(this, new SuperchatEventArg(obj));
                    break;
                //礼物
                case "SEND_GIFT":
                    MessageReceived(this, new SendGiftEventArgs(obj));
                    break;
                //舰组信息(上舰)
                case "GUARD_BUY":
                    MessageReceived(this, new GuardBuyEventArgs(obj));
                    break;
                //小时榜单变动通知
                case "ACTIVITY_BANNER_UPDATE_V2":
                    MessageReceived(this, new ActivityBannerEventArgs(obj));
                    break;
                //礼物combo
                case "COMBO_SEND":               
                    break;
                //进场特效
                case "ENTRY_EFFECT":
                    MessageReceived(this, new EntryEffectEventArgs(obj));
                    break;        
                //续费舰长
                case "USER_TOAST_MSG":
                    break;
                //在房间内续费了舰长
                case "NOTICE_MSG":
                    break;
                //欢迎
                case "WELCOME":
                    MessageReceived(this, new WelcomeEventArgs(obj));
                    break;
             
                //人气值(心跳数据)
                case "LiveP":
                    MessageReceived(this, new LivePopularity(obj));
                    break;
                //管理员警告
                case "WARNING":
                    MessageReceived(this, new WarningEventArg(obj));
                    break;
                //开播_心跳
                case "LIVE":
                    MessageReceived(this, new LiveEventArgs(obj));
                    break;
                //下播_心跳
                case "PREPARING":
                    MessageReceived(this, new PreparingpEventArgs(obj));
                    break;
                case "INTERACT_WORD":
                    //互动词，暂时不知道作用
                    break;
                case "PANEL":
                    //小时榜信息更新
                    break;
                case "ONLINE_RANK_COUNT":
                    //不知道是什么“在线等级计数”
                    break;
                case "ONLINE_RANK_V2":
                    //不知道是啥
                    break;
                case "ROOM_BANNER":
                    //房间横幅信息，应该就是置顶的那个跳转广告
                    break;
                default:
                    //Console.WriteLine(cmd);
                    MessageReceived(this, new MessageEventArgs(obj));
                    break;
            }
            return;
        }
        /// <summary>
        ///   替换部分字符串
        /// </summary>
        /// <param name="sPassed">需要替换的字符串</param>
        /// <returns></returns>
        public static string ReplaceString(string JsonString)
        {
            if (JsonString == null) { return JsonString; }
            if (JsonString.Contains("\\"))
            {
                JsonString = JsonString.Replace("\\", "\\\\");
            }
            //if (JsonString.Contains("\'"))
            //{
            //    JsonString = JsonString.Replace("\'", "\\\'");
            //}
            //if (JsonString.Contains("\""))
            //{
            //    JsonString = JsonString.Replace("\"", "\\\"");
            //}
            //去掉字符串的回车换行符
            JsonString = Regex.Replace(JsonString, @"[\n\r]", "");
            JsonString = JsonString.Trim();
            return JsonString;
        }
        private async Task _innerHeartbeat()
        {
            while (!m_innerRts.IsCancellationRequested)
            {
                try
                {
                    //UnityEngine.Debug.Log("heartbeat");
                    await _sendBinary(2, Encoding.UTF8.GetBytes("[object Object]"));
                    await Task.Delay(25 * 1000, m_innerRts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    string A = e.ToString();
                }
            }
        }

        private async Task _sendBinary(int type, byte[] body)
        {
            byte[] head = new byte[16];
            using (MemoryStream ms = new MemoryStream(head))
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    bw.WriteBE(16 + body.Length);
                    bw.WriteBE((ushort)16);
                    bw.WriteBE((ushort)1);
                    bw.WriteBE(type);
                    bw.WriteBE(1);
                }
            }
            await m_client.SendAsync(new ArraySegment<byte>(head), WebSocketMessageType.Binary, false, CancellationToken.None);
            await m_client.SendAsync(new ArraySegment<byte>(body), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private async Task _sendObject(int type, object obj)
        {

            //string jsonBody = JsonConvert.SerializeObject(obj, Formatting.None);
            string jsonBody = JsonMapper.ToJson(obj);
            await _sendBinary(type, System.Text.Encoding.UTF8.GetBytes(jsonBody));
        }

        private async Task<int> _getRealRoomId(int roomId)
        {
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage ret = await client.PostAsync("https://api.live.bilibili.com/room/v1/Room/room_init?id=" + roomId, new StringContent(""));
                string k = await ret.Content.ReadAsStringAsync();
                //dynamicClass cc = JsonConvert.DeserializeObject<dynamicClass>(k);
                dynamicClass cc = JsonMapper.ToObject<dynamicClass>(k);
                return cc.data.room_id;
            }
        }

        private class dynamicClass
        {
            public class d2
            {
                public int room_id { get; set; }
            }

            public d2 data { get; set; }
        }


        #region 新协议解析方法

        /// <summary>
        /// 消息协议
        /// </summary>
        public class DanmakuProtocol
        {
            /// <summary>
            /// 消息总长度 (协议头 + 数据长度)
            /// </summary>
            public int PacketLength;
            /// <summary>
            /// 消息头长度 (固定为16[sizeof(DanmakuProtocol)])
            /// </summary>
            public short HeaderLength;
            /// <summary>
            /// 消息版本号
            /// </summary>
            public short Version;
            /// <summary>
            /// 消息类型
            /// </summary>
            public int Operation;
            /// <summary>
            /// 参数, 固定为1
            /// </summary>
            public int Parameter;

            /// <summary>
            /// 转为本机字节序
            /// </summary>
            public DanmakuProtocol(byte[] buff)
            {
                PacketLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buff, 0));
                HeaderLength = IPAddress.HostToNetworkOrder(BitConverter.ToInt16(buff, 4));
                Version = IPAddress.HostToNetworkOrder(BitConverter.ToInt16(buff, 6));
                Operation = IPAddress.HostToNetworkOrder(BitConverter.ToInt32(buff, 8));
                Parameter = IPAddress.HostToNetworkOrder(BitConverter.ToInt32(buff, 12));
            }
        }

        /// <summary>
        /// 消息处理
        /// </summary>
        private void ProcessDanmakuData(int opt, byte[] buffer, int length)
        {
            switch (opt)
            {
                case 3:
                    {
                        if (length == 4)
                        {
                            int 人气值 = buffer[3] + buffer[2] * 255 + buffer[1] * 255 * 255 + buffer[0] * 255 * 255 * 255;
                            _parse("{\"cmd\":\"LiveP\",\"LiveP\":" + 人气值 + ",\"roomID\":" + TroomId + "}");
                        }
                        break;
                    }
                case 5:
                    {
                        try
                        {
                            string jsonBody = Encoding.UTF8.GetString(buffer, 0, length);
                            jsonBody = Regex.Unescape(jsonBody);
                            _parse(jsonBody);
                            //_parse("{\"cmd\":\"DDTV_T1\",\"T1\":1,\"roomID\":" + TroomId + "}");
                            //Debug.Log(jsonBody);
                            //ReceivedDanmaku?.Invoke(this, new ReceivedDanmakuArgs { Danmaku = new Danmaku(json) });
                        }
                        catch (Exception ex)
                        {
                            if (ex is Newtonsoft.Json.JsonException || ex is KeyNotFoundException)
                            {
                                //LogEvent?.Invoke(this, new LogEventArgs { Log = $@"[{_roomId}] 弹幕识别错误 {json}" });
                            }
                            else
                            {
                                //LogEvent?.Invoke(this, new LogEventArgs { Log = $@"[{_roomId}] {ex}" });
                            }
                        }
                        break;
                    }
                default:
                    break;
            }
        }

        /// <summary>
        /// 消息拆包
        /// </summary>
        private void DepackDanmakuData(byte[] messages)
        {

            byte[] headerBuffer = new byte[16];
            //for (int i = 0; i < 16; i++)
            //{
            //    headerBuffer[i] = messages[i];
            //}
            Array.Copy(messages, 0, headerBuffer, 0, 16);
            DanmakuProtocol protocol = new DanmakuProtocol(headerBuffer);

            //Debug.LogError(protocol.Version + "\\" + protocol.Operation);
            //

            if(Rooms.Rooms.RoomInfo.TryGetValue(mid,out Rooms.RoomInfoClass.RoomInfo roomInfo))
            {
                roomInfo.roomWebSocket.dokiTime= Tool.TimeModule.Time.Operate.GetRunMilliseconds();
            }
                

            if (protocol.PacketLength < 16)
            {
                //InfoLog.InfoPrintf($@"协议失败: (L:{protocol.PacketLength})", InfoLog.InfoClass.Debug);
                //InfoLog.InfoPrintf($@"{TroomId}房间收到协议PacketLength长度小于16，作为观测包更新心跳时间处理", InfoLog.InfoClass.Debug);
                //ProcessDanmakuData(99, null, 0);
                //throw new NotSupportedException($@"协议失败: (L:{protocol.PacketLength})");
                Console.WriteLine("<16");
                return;
            }
            int bodyLength = protocol.PacketLength - 16;
            if (bodyLength == 0)
            {
                //continue;
                return;
            }
            byte[] buffer = new byte[bodyLength];
            //for (int i = 0; i < bodyLength; i++)
            //{
            //    buffer[i] = messages[i + 16];
            //}
            Array.Copy(messages, 16, buffer, 0, bodyLength);
            //Console.WriteLine($"收到信息：Operation类型{messages[11]}，body内容[{Encoding.Default.GetString(buffer)}]");
            switch (protocol.Version)
            {
                case 1:
                    ProcessDanmakuData(protocol.Operation, buffer, bodyLength);
                    break;
                case 2:
                    {
                        var ms = new MemoryStream(buffer, 2, bodyLength - 2);
                        var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                        while (deflate.Read(headerBuffer, 0, 16) > 0)
                        {
                            protocol = new DanmakuProtocol(headerBuffer);
                            bodyLength = protocol.PacketLength - 16;
                            if (bodyLength == 0)
                            {
                                continue; // 没有内容了
                            }
                            if (buffer.Length < bodyLength) // 不够长再申请
                            {
                                buffer = new byte[bodyLength];
                            }
                            deflate.Read(buffer, 0, bodyLength);
                            ProcessDanmakuData(protocol.Operation, buffer, bodyLength);
                        }
                        ms.Dispose();
                        deflate.Dispose();
                        break;
                    }
                case 3:
                    ;
                    break;
                case 5:
                    ;
                    break;
                case 7:
                    ;
                    break;
                case 8:
                    ;
                    break;
                default:
                    ;
                    break;
            }
        }
        #endregion
     
    }
}
