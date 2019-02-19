using System;
using NetworkServer.Network;
using NetworkServer.ToLua;

namespace NetworkServer
{
    class Program
    {
        static void Main(string[] args)
        {
            NetworkManager.ConnectServer(1234);
        }
    }
}
