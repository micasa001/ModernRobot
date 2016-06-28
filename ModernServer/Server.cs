﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommonLib.Helpers;
using System.IO;
using System.Reflection;
using DBAccess;
using DBAccess.Database;

namespace ModernServer
{
    class Server
    {
        #region Logger settings
        private static Logger _logger = new Logger();

        #endregion

        private static DBActualizer _dbActualizer;

        static void Main(string[] args)
        { 
            _logger.Log(string.Format("Started server v.{0}", Assembly.GetExecutingAssembly().GetName().Version));
            _dbActualizer = new DBActualizer();
            _dbActualizer.Start();

            Console.ReadLine();
            _dbActualizer.Stop();
        }
    }
}
