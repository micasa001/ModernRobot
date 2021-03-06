﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DBAccess;
using CommonLib.Helpers;
using Calculator.Strategies;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;

namespace Calculator.Calculation
{
    public class CalculationOrdersPool : ICalculationOrdersPool, IDisposable
    {
        private DBDataReader _reader = new DBDataReader();
        private Type _strategyType;
        private ConcurrentQueue<CalculationOrder> _ordersQueue = new ConcurrentQueue<CalculationOrder>();
        private List<CalculationOrder> _finishedOrders = new List<CalculationOrder>();

        private const int nightHoursStart = 23;
        private const int nightHoursEnd = 10;

        public bool IsProcessingOrders { get; private set; }

        public void Lock()
        {
            IsProcessingOrders = true;
        }

        public void UnLock()
        {
            IsProcessingOrders = false;
        }

        public CalculationOrdersPool(Type strategyType)
        {
            _strategyType = strategyType;
        }

        public int WaitingOrdersCount
        {
            get
            {
                return _ordersQueue.Count();
            }
        }

        public CalculationOrder[] FinishedOrders
        {
            get
            {
                return _finishedOrders.ToArray();
            }
        }

        public Guid AddNewOrderForCalculation(string insName, DateTime dateFrom, DateTime dateTo, TimePeriods period, float[] parameters, float stopLoss, bool ignoreNightCandles, float daySpread, float nightSpread)
        {
            var order = CalculationOrder.CreateNew(insName, dateFrom, dateTo, period, parameters);
            order.StopLoss = stopLoss;
            order.IgnoreNightCandles = ignoreNightCandles;
            order.DaySpread = daySpread;
            order.NightSpread = nightSpread;
            _ordersQueue.Enqueue(order);
            return order.Id;
        }

        public void Dispose()
        {
            _reader.Dispose();
        }

        public void Flush()
        {
            _ordersQueue = new ConcurrentQueue<CalculationOrder>();
            _finishedOrders.Clear();
        }

        public void ProcessOrders()
        {
            ThreadPool.QueueUserWorkItem(obj =>
            {
                IsProcessingOrders = true;
                while (!_ordersQueue.IsEmpty)
                {
                    try
                    {
                        CalculationOrder order;
                        if (_ordersQueue.TryDequeue(out order))
                            CalculateNextOrder(order);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(string.Format(" ERROR on calculation: {0}", ex));
                    }
                }
                IsProcessingOrders = false;
            });
        }

        public void GetFinishedOrderResults(Guid orderId)
        {
            var order = FinishedOrders.SingleOrDefault(o => o.Id == orderId);
            if (order != null)
            {
                try
                {
                    CalculateNextOrder(order, true);
                }
                catch (Exception ex)
                {
                    Logger.Log(string.Format(" ERROR on finished order recalculation: {0}", ex));
                }
            }
        }

        private bool IsInDay(DateTime dateTimeStamp)
        {
            return (dateTimeStamp.Hour < nightHoursStart) && (dateTimeStamp.Hour >= nightHoursEnd);
        }

        private void CalculateNextOrder(CalculationOrder order, bool saveResults = false)
        {
            var datefrom = order.DateFrom.AddMonths(-3);
            if (_reader.GetMinDateTimeStamp(order.InstrumentName) > datefrom)
            {
                Logger.Log(" ERROR: incorrect data in order DATE FROM.");
                return;
            }
            var candles = _reader.GetCandles(order.InstrumentName, order.Period, datefrom, order.DateTo);

            if (order.IgnoreNightCandles)
                candles = candles.Where(o => o.DateTimeStamp.Hour <= nightHoursStart && o.DateTimeStamp.Hour >= nightHoursEnd).ToArray();

            var tickers = candles.Select(o => o.Ticker).Distinct()
                .OrderBy(o => candles.Where(c => c.Ticker == o).Max(d => d.DateTimeStamp)).ToArray();
            var strategy = (IStrategy)Activator.CreateInstance(_strategyType);
            for (var i = 0; i < order.Parameters.Count(); i++)
                strategy.Parameters[i].Value = order.Parameters[i];
            var outDatas = new List<object[]>();
            var balances = new List<float>();
            var balancesPerDeal = new List<float>();
            var lastResult = StrategyResult.Exit;
            var balance = 0f;
            var priceDiff = 0f;
            var lotSize = 0;
            var lastPrice = 0f;
            var stopPrice = 0f;
            var gapValue = float.MinValue;
            float maxBalancePerDeal = float.MinValue;

            foreach (var ticker in tickers)
            {
                var tc = candles.Where(o => o.Ticker == ticker).OrderBy(o => o.DateTimeStamp).ToList();
                lastResult = StrategyResult.Exit;
                var itemDateFrom = _reader.GetItemDateFrom(ticker);
                if (order.DateFrom > itemDateFrom)
                    itemDateFrom = order.DateFrom;
                var startIndex = tc.FindIndex(o => o.DateTimeStamp >= itemDateFrom);
                if (startIndex == -1)
                    continue;
                if (strategy.AnalysisDataLength > startIndex)
                {
                    Logger.Log(string.Format("ERROR: order id {0}, preload data count < data count needed for analysis!", order.Id));
                    return;
                }
                strategy.Initialize();
                var currentSL = 0f;
                strategy.StopLossValue = order.StopLoss;

                EventHandler<float> stopLossChanged = (sender, newVal) =>
                {
                    if (currentSL != 0)
                    {
                        if (lastResult == StrategyResult.Long)
                            if (newVal > lastPrice - order.StopLoss)
                                currentSL = newVal;
                        if (lastResult == StrategyResult.Short)
                            if (newVal < lastPrice + order.StopLoss)
                                currentSL = newVal;
                    }
                };

                strategy.OnStopLossChanged += stopLossChanged;

                for (var i = startIndex; i < tc.Count; i++)
                {
                    var data = tc.GetRange(i - strategy.AnalysisDataLength + 1, strategy.AnalysisDataLength).ToArray();
                    object[] outData;
                    var result = strategy.Analyze(data, out outData);

                    if ((i == tc.Count - 1) || ((tc[i].DateTimeStamp.Hour == nightHoursStart) && (order.IgnoreNightCandles)))
                        result = StrategyResult.Exit;

                    if (lastResult != result)
                    {
                        var closePrice = tc[i].Close;
                        if (lastResult == StrategyResult.Long)
                            closePrice = closePrice - (IsInDay(tc[i].DateTimeStamp) ? order.DaySpread : order.NightSpread);
                        if (lastResult == StrategyResult.Short)
                            closePrice = closePrice + (IsInDay(tc[i].DateTimeStamp) ? order.DaySpread : order.NightSpread);

                        balance = balance + priceDiff + lotSize * closePrice;

                        if (result == StrategyResult.Long)
                        {
                            lotSize = 1;
                            priceDiff = -(tc[i].Close + (IsInDay(tc[i].DateTimeStamp) ? order.DaySpread : order.NightSpread)) * lotSize;
                            if (order.StopLoss == 0)
                                currentSL = 0;
                            else
                                currentSL = tc[i].Close - order.StopLoss;
                        }
                        if (result == StrategyResult.Short)
                        {
                            lotSize = -1;
                            priceDiff = (tc[i].Close - (IsInDay(tc[i].DateTimeStamp) ? order.DaySpread : order.NightSpread)) * (-lotSize);
                            if (order.StopLoss == 0)
                                currentSL = 0;
                            else
                                currentSL = tc[i].Close + order.StopLoss;
                        }
                        if (result == StrategyResult.Exit)
                        {
                            priceDiff = 0;
                            lotSize = 0;
                            currentSL = 0;
                        }
                        balances.Add(balance);
                        lastPrice = tc[i].Close;
                        stopPrice = 0;
                        balancesPerDeal.Add(balance);
                    }
                    else
                    {
                        balances.Add(balance + priceDiff + lotSize * tc[i].Close);
                        if (currentSL != 0)
                        {
                            if (result == StrategyResult.Long)
                                if (tc[i].Close <= currentSL)
                                {
                                    balance = balance + priceDiff + lotSize * (tc[i].Close - (IsInDay(tc[i].DateTimeStamp) ? order.DaySpread : order.NightSpread));
                                    currentSL = 0;
                                    priceDiff = 0;
                                    lotSize = 0;
                                    stopPrice = tc[i].Close;
                                }
                            if (result == StrategyResult.Short)
                                if (tc[i].Close >= currentSL)
                                {
                                    balance = balance + priceDiff + lotSize * (tc[i].Close + (IsInDay(tc[i].DateTimeStamp) ? order.DaySpread : order.NightSpread));
                                    currentSL = 0;
                                    priceDiff = 0;
                                    lotSize = 0;
                                    stopPrice = tc[i].Close;
                                }
                        }
                    }
                    lastResult = result;

                    var outList = new List<object>() { data.Last().DateTimeStamp, tc[i].Ticker, tc[i].Open, tc[i].High, tc[i].Low, tc[i].Close };
                    outList.AddRange(outData);
                    outList.Add(lotSize);
                    if (result == StrategyResult.Long)
                        outList.Add(currentSL);
                    if (result == StrategyResult.Short)
                        outList.Add(currentSL);
                    if (result == StrategyResult.Exit)
                        outList.Add(0);
                    if (stopPrice == 0)
                        outList.Add("No");
                    else
                        outList.Add(string.Format("Yes ({0})", stopPrice));
                    outList.Add(balance);
                    if (saveResults)
                        outDatas.Add(outList.ToArray());
                    if (balancesPerDeal.Any())
                    {
                        if (maxBalancePerDeal < balancesPerDeal.Last())
                            maxBalancePerDeal = balancesPerDeal.Last();
                        if (gapValue < maxBalancePerDeal - balancesPerDeal.Last())
                            gapValue = maxBalancePerDeal - balancesPerDeal.Last();
                    }
                }
                strategy.OnStopLossChanged -= stopLossChanged;
            }
            var outDataDescription = new List<string>();
            if (saveResults)
            {
                outDataDescription.AddRange(new string[6] { "Date Time", "Ticker", "Open", "High", "Low", "Close" });
                outDataDescription.AddRange(strategy.OutDataDescription);
                outDataDescription.Add("LOT Size");
                outDataDescription.Add("STOP price");
                outDataDescription.Add("STOPPED");
                outDataDescription.Add("Balance per deal");
            }
            

            if (!saveResults)
                balances.Clear();

            order.Result = new CalculationResult() { OutData = outDatas.Select(o => o.Select(obj => obj.ToString()).ToArray()).ToArray(), Balances = balances.ToArray(), OutDataDescription = outDataDescription.ToArray() };
            order.TotalBalance = balance;
            order.Gap = gapValue;
            
            order.Status = CalculationOrderStatus.Finished;
            if (!saveResults)
                _finishedOrders.Add(order);
        }
    }
}
