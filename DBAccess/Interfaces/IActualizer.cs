﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DBAccess.Interfaces
{
    public interface IActualizer
    {
        void ActualizeDB();
        bool CheckDBIntegrity();
        void Start();
        void Stop();
    }
}