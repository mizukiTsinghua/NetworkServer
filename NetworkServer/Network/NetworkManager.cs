using NetworkServer.Common;
using NetworkServer.Handler;
using NetworkServer.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace NetworkServer.Network
{
    internal sealed partial class NetworkManager
    {
        //监听Socket
        public static Socket listenSocket;
        
        //客户端Socket及状态信息
        public static Dictionary<Socket, ClientState> clients = new Dictionary<Socket, ClientState>();
        
        //ping间隔
        public static long pingInterval = 30;
        
        //Select的检查列表
        private static List<Socket> checkRead = new List<Socket>();

        /// <summary>
        ///  发送缓冲区
        ///  发送数据时，数据会被写入队列的末尾，根据队列是否为空判断是否回调
        /// </summary>
        private static Queue<ByteArray> writeQueue = new Queue<ByteArray>();

        /// <summary>
        /// 多路复用Selecter
        /// </summary>
        /// <param name="listenPort"></param>
        public static void ConnectServer(int listenPort)
        {
            listenSocket = new Socket(AddressFamily.InterNetwork,
                SocketType.Stream, ProtocolType.Tcp);

            IPAddress ipAdr = IPAddress.Parse("192.168.0.104");
            IPEndPoint ipEp = new IPEndPoint(ipAdr, listenPort);
            listenSocket.Bind(ipEp);

            listenSocket.Listen(0);
            Console.WriteLine("[服务器]启动成功");

            while (true)
            {
                //重置checkRead
                ResetCheckRead();  
                Socket.Select(checkRead, null, null, 1000);
                
                //检查可读对象
                for (int i = checkRead.Count - 1; i >= 0; --i)
                {
                    Socket s = checkRead[i];
                    if (s == listenSocket)
                    {
                        ReadListenSocket(s);
                    }
                    else
                    {
                        ReadClientSocket(s);
                    }
                }
                //超时
                Timer();
            }
        }

        /// <summary>
        /// 重置checkRead列表
        /// </summary>
        public static void ResetCheckRead()
        {
            checkRead.Clear();
            checkRead.Add(listenSocket);
            foreach (ClientState s in clients.Values)
            {
                checkRead.Add(s.socket);
            }
        }

        /// <summary>
        /// 读取监听Socket，如果可读，说明有客户端连接
        /// </summary>
        /// <param name="listenfd"></param>
        public static void ReadListenSocket(Socket listenfd)
        {
            try
            {
                Socket clientfd = listenfd.Accept();
                Console.WriteLine("Accept " + clientfd.RemoteEndPoint.ToString());
                ClientState state = new ClientState();
                state.socket = clientfd;
                state.lastPingTime = GetTimeStamp();
                clients.Add(clientfd, state);
            }
            catch (SocketException ex)
            {
                Console.WriteLine("Accept fail" + ex.ToString());
            }
        }

        /// <summary>
        /// 读取客户端Socket，如果可读，说明客户端发送消息
        /// </summary>
        /// <param name="clientfd"></param>
        public static void ReadClientSocket(Socket clientfd)
        {
            ClientState state = clients[clientfd];
            ByteArray readBuff = state.readBuff;

            int count = 0;
            
            //缓冲区不够，清除，若依旧不够，只能返回
            //当单条协议超过缓冲区长度时会发生
            if (readBuff.remain <= 0)
            {
                OnReceiveData(state);
                readBuff.MoveBytes();
            }
            if (readBuff.remain <= 0)
            {
                Console.WriteLine("Receive fail , maybe msg length > buff capacity");
                Close(state);
                return;
            }
            try
            {
                count = clientfd.Receive(readBuff.bytes, readBuff.writeIdx, readBuff.remain, 0);
            }
            catch (SocketException ex)
            {
                Console.WriteLine("Receive SocketException " + ex.ToString());
                Close(state);
                return;
            }
            if (count <= 0)
            {
                Console.WriteLine("Socket Close " + clientfd.RemoteEndPoint.ToString());
                Close(state);
                return;
            }
            //消息处理
            readBuff.writeIdx += count;
            //处理二进制消息
            OnReceiveData(state);
            //移动缓冲区
            readBuff.CheckAndMoveBytes();
        }


        /// <summary>
        /// 数据处理
        /// </summary>
        /// <param name="state"></param>
        public static void OnReceiveData(ClientState state)
        {
            ByteArray readBuff = state.readBuff;
            //消息长度
            if (readBuff.length <= 2)
            {
                return;
            }
            //消息体长度
            int readIdx = readBuff.readIdx;
            byte[] bytes = readBuff.bytes;
            Int16 bodyLength = (Int16)((bytes[readIdx + 1] << 8) | bytes[readIdx]);
            if (readBuff.length < bodyLength)
            {
                return;
            }
            readBuff.readIdx += 2;
            //解析协议名
            int nameCount = 0;
            string protoName = ProtocolBase.DecodeName(readBuff.bytes, readBuff.readIdx, out nameCount);
            if (protoName == "")
            {
                Console.WriteLine("OnReceiveData MsgBase.DecodeName fail");
                Close(state);
                return;
            }
            readBuff.readIdx += nameCount;
            //解析协议体
            int bodyCount = bodyLength - nameCount;
            if (bodyCount <= 0)
            {
                Console.WriteLine("OnReceiveData fail, bodyCount <=0 ");
                Close(state);
                return;
            }
            ProtocolBase msgBase = ProtocolBase.Decode(protoName, readBuff.bytes, readBuff.readIdx, bodyCount);
            readBuff.readIdx += bodyCount;
            readBuff.CheckAndMoveBytes();
            //分发消息
            MethodInfo mi = typeof(MsgHandler).GetMethod(protoName);
            object[] o = { state, msgBase };
            Console.WriteLine("Receive " + protoName);
            if (mi != null)
            {
                mi.Invoke(null, o);
            }
            else
            {
                Console.WriteLine("OnReceiveData Invoke fail " + protoName);
            }
            //继续读取消息
            if (readBuff.length > 2)
            {
                OnReceiveData(state);
            }
        }

        /// <summary>
        /// 发送
        /// </summary>
        /// <param name="cs"></param>
        /// <param name="msg"></param>
        public static void Send(ClientState cs, ProtocolBase msg)
        {
            //状态判断
            if (cs == null)
            {
                return;
            }
            if (!cs.socket.Connected)
            {
                return;
            }
            //数据编码
            byte[] nameBytes = ProtocolBase.EncodeName(msg);
            byte[] bodyBytes = ProtocolBase.Encode(msg);
            int len = nameBytes.Length + bodyBytes.Length;
            byte[] sendBytes = new byte[2 + len];
           
            //组装数据长度
            sendBytes[0] = (byte)(len % 256);
            sendBytes[1] = (byte)(len / 256);
            
            //组装数据名字
            Array.Copy(nameBytes, 0, sendBytes, 2, nameBytes.Length);
            
            //组装数据消息体
            Array.Copy(bodyBytes, 0, sendBytes, 2 + nameBytes.Length, bodyBytes.Length);
            ByteArray ba = new ByteArray(sendBytes);
            writeQueue.Enqueue(ba);

            try
            {
                cs.socket.BeginSend(sendBytes, 0, sendBytes.Length, 0, null, null);
            }
            catch (SocketException ex)
            {
                Console.WriteLine("Socket Close on BeginSend" + ex.ToString());
            }

        }

        /// <summary>
        /// 发送回调
        /// 解决数据发送不完整
        /// </summary>
        /// <param name="async"></param>
        public static void SendCallBack(IAsyncResult async)
        {
            Socket socket = async.AsyncState as Socket;
            int count = socket.EndSend(async);
            ByteArray ba = writeQueue.First();
            ba.readIdx += count;
            if(ba.length==0)
            {
                writeQueue.Dequeue();
                ba = writeQueue.First();
            }
            if (null != ba)
            {
                socket.BeginSend(ba.bytes,ba.readIdx,ba.length,0,SendCallBack,socket);
            }
        }

          /// <summary>
          /// 关闭连接
          /// </summary>
          /// <param name="state"></param>
        public static void Close(ClientState state)
        {
            //消息分发
            MethodInfo mei = typeof(EventHandler).GetMethod("OnDisconnect");
            object[] ob = { state };
            mei.Invoke(null, ob);
            //关闭
            state.socket.Close();
            clients.Remove(state.socket);

        }

        /// <summary>
        /// 定时器
        /// </summary>
        static void Timer()
        {
            //消息分发
            MethodInfo mei = typeof(EventHandler).GetMethod("OnTimer");
            object[] ob = { };
            mei.Invoke(null, ob);
        }

        /// <summary>
        /// 时间戳
        /// </summary>
        /// <returns></returns>
        public static long GetTimeStamp()
        {
            TimeSpan ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            return Convert.ToInt64(ts.TotalSeconds);
        }
    }
}
