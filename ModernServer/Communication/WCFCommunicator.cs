﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Text;
using CommonLib.Helpers;
using ModernServer.WCFEntities;
using DBAccess;
using Calculator.Strategies;
using Calculator.Calculation;

namespace ModernServer.Communication
{
    public class WCFCommunicator : IWCFCommunicator, IDisposable
    {
        private static List<RemoteCalculation> _remoteCalculators = new List<RemoteCalculation>();

        private DBDataReader _reader = new DBDataReader();
        private readonly IStrategy[] _avaliableStrategies = { new FortsBasic() };

        public ActualizedInstrument[] GetActualizedInstruments()
        {
            return
            _reader.GetAvaliableInstrumentNames().Select(o => new ActualizedInstrument()
            {
                Name = o,
                DateFrom = _reader.GetMinDateTimeStamp(o).AddMonths(3),
                DateTo = _reader.GetMaxDateTimeStamp(o)
            }).ToArray();
        }

        public void Dispose()
        {
            _reader.Dispose();
        }

        public string[] GetAvaliableStrategies()
        {
            return _avaliableStrategies.Select(o => o.Name).ToArray();    
        }

        public string[] GetStrategyParametersDescription(string strategyName)
        {
            var strategy = _avaliableStrategies.SingleOrDefault(o => o.Name==strategyName);
            if (strategy != null)
                return strategy.Parameters.Select(o => o.Description).ToArray();
            return null;

        }
        public RemoteCalculationInfo[] GetRemoteCalculationsInfo()
        {
            return _remoteCalculators.Select(o => new RemoteCalculationInfo(o.Id, o.Name, o.StrategyName) { WaitingOrdersCount = o.WaitingOrdersCount, FinishedOrdersCount = o.FinishedOrdersCount }).ToArray();
        }

        public RemoteCalculationInfo AddRemoteCalculation(string name, string strategyName)
        {
            var strategy = _avaliableStrategies.SingleOrDefault(o => o.Name == strategyName);
            if (strategy == null)
                return null;
            var strategyType = strategy.GetType();
            var rCalc = new RemoteCalculation(name, strategyType);
            _remoteCalculators.Add(rCalc);
            return new RemoteCalculationInfo(rCalc.Id, rCalc.Name, rCalc.StrategyName) { WaitingOrdersCount = rCalc.WaitingOrdersCount, FinishedOrdersCount = rCalc.FinishedOrdersCount };
        }

        public void RemoveRemoteCalculation(Guid id)
        {
            var rCalc = _remoteCalculators.SingleOrDefault(o => o.Id == id);
            if (rCalc != null)
                _remoteCalculators.Remove(rCalc);
        }

        public void AddOrderToRemoteCalulation(Guid idCalculation, string insName, DateTime dateFrom, DateTime dateTo, TimePeriods period, float[] parameters)
        {
            var rc = _remoteCalculators.Single(o => o.Id == idCalculation);
            if (rc == null)
                return;
            rc.OrdersPool.AddNewOrderForCalculation(insName, dateFrom, dateTo, period, parameters);
        }

        public void StartRemoteCalculation(Guid idCalculation)
        {
            var rc = _remoteCalculators.Single(o => o.Id == idCalculation);
            if (rc == null)
                return;
            rc.OrdersPool.ProcessOrders();
        }

        public void StopRemoteCalculation(Guid idCalculation)
        {
            var rc = _remoteCalculators.Single(o => o.Id == idCalculation);
            if (rc == null)
                return;
            rc.OrdersPool.Flush();
        }

        public CalculationOrder[] GetFinishedOrdersForRemoteCalculation(Guid idCalculation)
        {
            var rc = _remoteCalculators.Single(o => o.Id == idCalculation);
            if (rc == null)
                return null;
            return rc.OrdersPool.FinishedOrders;
        }

        public int GetWaitingOrdersForRemoteCalculation(Guid idCalculation)
        {
            var rc = _remoteCalculators.Single(o => o.Id == idCalculation);
            if (rc == null)
                return 0;
            return rc.OrdersPool.WaitingOrdersCount;
        }
    }
}
