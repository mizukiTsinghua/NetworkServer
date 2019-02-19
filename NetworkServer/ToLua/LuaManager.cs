using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LuaInterface;


namespace NetworkServer.ToLua
{
    public class LuaManager
    {
        private static Lua m_Lua = new Lua();

        public static Lua LuaState
        {
            get => m_Lua;
        }

        /// <summary>
        /// 加载全部Lua文件
        /// 这里加载指定目录下Lua文件，将所有Lua文件添加到集合中，遍历执行
        /// </summary>
        public static void LoadLua()
        {

        }

        public static void DoLuaCode(string luaCode)
        {
            if (null == m_Lua)
                return;

            m_Lua.DoString(luaCode);
        }

        public static void DoLuaFile(string fileName)
        {
            if (null == m_Lua)
                return;

            m_Lua.DoFile(fileName);
        }
    }
}
