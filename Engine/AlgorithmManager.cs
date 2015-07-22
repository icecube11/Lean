﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Fasterflect;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.RealTime;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.Lean.Engine
{
    /// <summary>
    /// Algorithm manager class executes the algorithm and generates and passes through the algorithm events.
    /// </summary>
    public class AlgorithmManager
    {
        private DateTime _previousTime;
        private AlgorithmStatus _algorithmState = AlgorithmStatus.Running;
        private readonly object _lock = new object();
        private string _algorithmId = "";
        private DateTime _currentTimeStepTime;
        private readonly TimeSpan _timeLoopMaximum = TimeSpan.FromMinutes(Config.GetDouble("algorithm-manager-time-loop-maximum", 10));
        private long _dataPointCount;

        /// <summary>
        /// Publicly accessible algorithm status
        /// </summary>
        public AlgorithmStatus State
        {
            get
            {
                return _algorithmState;
            }
        }

        /// <summary>
        /// Public access to the currently running algorithm id.
        /// </summary>
        public string AlgorithmId
        {
            get
            {
                return _algorithmId;
            }
        }

        /// <summary>
        /// Gets the amount of time spent on the current time step
        /// </summary>
        public TimeSpan CurrentTimeStepElapsed
        {
            get { return _currentTimeStepTime == DateTime.MinValue ? TimeSpan.Zero : DateTime.UtcNow - _currentTimeStepTime; }
        }

        /// <summary>
        /// Gets a function used with the Isolator for verifying we're not spending too much time in each
        /// algo manager timer loop
        /// </summary>
        public readonly Func<string> TimeLoopWithinLimits;

        private readonly bool _liveMode;

        /// <summary>
        /// Quit state flag for the running algorithm. When true the user has requested the backtest stops through a Quit() method.
        /// </summary>
        /// <seealso cref="QCAlgorithm.Quit"/>
        public bool QuitState
        {
            get
            {
                return _algorithmState == AlgorithmStatus.Deleted;
            }
        }

        /// <summary>
        /// Gets the number of data points processed per second
        /// </summary>
        public long DataPoints
        {
            get
            {
                return _dataPointCount;
            }
        }

        public AlgorithmManager(bool liveMode)
        {
            TimeLoopWithinLimits = () =>
            {
                if (CurrentTimeStepElapsed > _timeLoopMaximum)
                {
                    return "Algorithm took longer than 10 minutes on a single time loop.";
                }
                return null;
            };
            _liveMode = liveMode;
        }

        /// <summary>
        /// Launch the algorithm manager to run this strategy
        /// </summary>
        /// <param name="job">Algorithm job</param>
        /// <param name="algorithm">Algorithm instance</param>
        /// <param name="feed">Datafeed object</param>
        /// <param name="transactions">Transaction manager object</param>
        /// <param name="results">Result handler object</param>
        /// <param name="realtime">Realtime processing object</param>
        /// <param name="token">Cancellation token</param>
        /// <remarks>Modify with caution</remarks>
        public void Run(AlgorithmNodePacket job, IAlgorithm algorithm, IDataFeed feed, ITransactionHandler transactions, IResultHandler results, IRealTimeHandler realtime, CancellationToken token) 
        {
            //Initialize:
            _dataPointCount = 0;
            var startingPortfolioValue = algorithm.Portfolio.TotalPortfolioValue;
            var backtestMode = (job.Type == PacketType.BacktestNode);
            var methodInvokers = new Dictionary<Type, MethodInvoker>();
            var marginCallFrequency = TimeSpan.FromMinutes(5);
            var nextMarginCallTime = DateTime.MinValue;

            //Initialize Properties:
            _algorithmId = job.AlgorithmId;
            _algorithmState = AlgorithmStatus.Running;
            _previousTime = algorithm.StartDate.Date;

            //Create the method accessors to push generic types into algorithm: Find all OnData events:

            // Algorithm 2.0 data accessors
            var hasOnDataTradeBars = AddMethodInvoker<TradeBars>(algorithm, methodInvokers);
            var hasOnDataTicks = AddMethodInvoker<Ticks>(algorithm, methodInvokers);

            // dividend and split events
            var hasOnDataDividends = AddMethodInvoker<Dividends>(algorithm, methodInvokers);
            var hasOnDataSplits = AddMethodInvoker<Splits>(algorithm, methodInvokers);

            // Algorithm 3.0 data accessors
            var hasOnDataSlice = algorithm.GetType().GetMethods()
                .Where(x => x.Name == "OnData" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof (Slice))
                .FirstOrDefault(x => x.DeclaringType == algorithm.GetType()) != null;

            //Go through the subscription types and create invokers to trigger the event handlers for each custom type:
            foreach (var config in feed.Subscriptions) 
            {
                //If type is a tradebar, combine tradebars and ticks into unified array:
                if (config.Type.Name != "TradeBar" && config.Type.Name != "Tick") 
                {
                    //Get the matching method for this event handler - e.g. public void OnData(Quandl data) { .. }
                    var genericMethod = (algorithm.GetType()).GetMethod("OnData", new[] { config.Type });

                    //If we already have this Type-handler then don't add it to invokers again.
                    if (methodInvokers.ContainsKey(config.Type)) continue;

                    //If we couldnt find the event handler, let the user know we can't fire that event.
                    if (genericMethod == null && !hasOnDataSlice)
                    {
                        algorithm.RunTimeError = new Exception("Data event handler not found, please create a function matching this template: public void OnData(" + config.Type.Name + " data) {  }");
                        _algorithmState = AlgorithmStatus.RuntimeError;
                        return;
                    }
                    if (genericMethod != null)
                    {
                        methodInvokers.Add(config.Type, genericMethod.DelegateForCallMethod());
                    }
                }
            }

            //Loop over the queues: get a data collection, then pass them all into relevent methods in the algorithm.
            Log.Trace("AlgorithmManager.Run(): Begin DataStream - Start: " + algorithm.StartDate + " Stop: " + algorithm.EndDate);
            foreach (var timeSlice in feed.Bridge.GetConsumingEnumerable(token))
            {
                // reset our timer on each loop
                _currentTimeStepTime = DateTime.UtcNow;

                //Check this backtest is still running:
                if (_algorithmState != AlgorithmStatus.Running)
                {
                    Log.Error(string.Format("AlgorithmManager.Run(): Algorthm state changed to {0} at {1}", _algorithmState, timeSlice.Time));
                    break;
                }

                //Execute with TimeLimit Monitor:
                if (token.IsCancellationRequested)
                {
                    Log.Error("AlgorithmManager.Run(): CancellationRequestion at " + timeSlice.Time);
                    return;
                }

                var time = timeSlice.Time;
                var newData = timeSlice.Data;

                //If we're in backtest mode we need to capture the daily performance. We do this here directly
                //before updating the algorithm state with the new data from this time step, otherwise we'll
                //produce incorrect samples (they'll take into account this time step's new price values)
                if (backtestMode)
                {
                    //On day-change sample equity and daily performance for statistics calculations
                    if (_previousTime.Date != time.Date)
                    {
                        //Sample the portfolio value over time for chart.
                        results.SampleEquity(_previousTime, Math.Round(algorithm.Portfolio.TotalPortfolioValue, 4));
                        
                        
                        //Check for divide by zero
                        if (startingPortfolioValue == 0m)
                        {
                            results.SamplePerformance(_previousTime.Date, 0);
                        }
                        else
                        {
                            results.SamplePerformance(_previousTime.Date, Math.Round((algorithm.Portfolio.TotalPortfolioValue - startingPortfolioValue) * 100 / startingPortfolioValue, 10));
                        }
                        startingPortfolioValue = algorithm.Portfolio.TotalPortfolioValue;
                    }
                }
                

                //Update algorithm state after capturing performance from previous day

                //Set the algorithm and real time handler's time
                algorithm.SetDateTime(time);
                realtime.SetTime(time);

                //On each time step push the real time prices to the cashbook so we can have updated conversion rates
                algorithm.Portfolio.CashBook.Update(newData);

                //Update the securities properties: first before calling user code to avoid issues with data
                algorithm.Securities.Update(time, newData);

                if (!backtestMode)
                {
                    results.SampleBenchmark(time, algorithm.Benchmark(time));
                }else if (_previousTime.Date != time.Date)
                {
                    results.SampleBenchmark(time, algorithm.Benchmark(time));
                }

                // process fill models on the updated data before entering algorithm, applies to all non-market orders
                transactions.ProcessSynchronousEvents();

                //Check if the user's signalled Quit: loop over data until day changes.
                if (algorithm.GetQuit())
                {
                    _algorithmState = AlgorithmStatus.Quit;
                    Log.Trace("AlgorithmManager.Run(): Algorithm quit requested.");
                    break;
                }
                if (algorithm.RunTimeError != null)
                {
                    _algorithmState = AlgorithmStatus.RuntimeError;
                    Log.Trace(string.Format("AlgorithmManager.Run(): Algorithm encountered a runtime error at {0}. Error: {1}", timeSlice.Time, algorithm.RunTimeError));
                    break;
                }

                // perform margin calls, in live mode we can also use realtime to emit these
                if (time >= nextMarginCallTime || (_liveMode && nextMarginCallTime > DateTime.Now))
                {
                    // determine if there are possible margin call orders to be executed
                    bool issueMarginCallWarning;
                    var marginCallOrders = algorithm.Portfolio.ScanForMarginCall(out issueMarginCallWarning);
                    if (marginCallOrders.Count != 0)
                    {
                        try
                        {
                            // tell the algorithm we're about to issue the margin call
                            algorithm.OnMarginCall(marginCallOrders);
                        }
                        catch (Exception err)
                        {
                            algorithm.RunTimeError = err;
                            _algorithmState = AlgorithmStatus.RuntimeError;
                            Log.Error("AlgorithmManager.Run(): RuntimeError: OnMarginCall: " + err.Message + " STACK >>> " + err.StackTrace);
                            return;
                        }

                        // execute the margin call orders
                        var executedTickets = algorithm.Portfolio.MarginCallModel.ExecuteMarginCall(marginCallOrders);
                        foreach (var ticket in executedTickets)
                        {
                            algorithm.Error(string.Format("{0} - Executed MarginCallOrder: {1} - Quantity: {2} @ {3}", algorithm.Time, ticket.Symbol, ticket.Quantity, ticket.OrderEvents.Last().FillPrice));
                        }
                    }
                    // we didn't perform a margin call, but got the warning flag back, so issue the warning to the algorithm
                    else if (issueMarginCallWarning)
                    {
                        try
                        {
                            algorithm.OnMarginCallWarning();
                        }
                        catch (Exception err)
                        {
                            algorithm.RunTimeError = err;
                            _algorithmState = AlgorithmStatus.RuntimeError;
                            Log.Error("AlgorithmManager.Run(): RuntimeError: OnMarginCallWarning: " + err.Message + " STACK >>> " + err.StackTrace);
                        }
                    }

                    nextMarginCallTime = time + marginCallFrequency;
                }

                //Trigger the data events: Invoke the types we have data for:
                var newBars = new TradeBars(time);
                var newTicks = new Ticks(time);
                var newDividends = new Dividends(time);
                var newSplits = new Splits(time);

                //Invoke all non-tradebars, non-ticks methods and build up the TradeBars and Ticks dictionaries
                // --> i == Subscription Configuration Index, so we don't need to compare types.
                foreach (var i in newData.Keys)
                {
                    //Data point and config of this point:
                    var dataPoints = newData[i];
                    var config = feed.Subscriptions[i];

                    //Keep track of how many data points we've processed
                    _dataPointCount += dataPoints.Count;

                    //We don't want to pump data that we added just for currency conversions
                    if (config.IsInternalFeed)
                    {
                        continue;
                    }

                    //Create TradeBars Unified Data --> OR --> invoke generic data event. One loop.
                    //  Aggregate Dividends and Splits -- invoke portfolio application methods
                    foreach (var dataPoint in dataPoints)
                    {
                        var dividend = dataPoint as Dividend;
                        if (dividend != null)
                        {
                            Log.Trace("AlgorithmManager.Run(): Applying Dividend for " + dividend.Symbol);
                            // if this is a dividend apply to portfolio
                            algorithm.Portfolio.ApplyDividend(dividend);
                            if (hasOnDataDividends)
                            {
                                // and add to our data dictionary to pump into OnData(Dividends data)
                                newDividends.Add(dividend);
                            }
                            continue;
                        }

                        var split = dataPoint as Split;
                        if (split != null)
                        {
                            Log.Trace("AlgorithmManager.Run(): Applying Split for " + split.Symbol);

                            // if this is a split apply to portfolio
                            algorithm.Portfolio.ApplySplit(split);
                            if (hasOnDataSplits)
                            {
                                // and add to our data dictionary to pump into OnData(Splits data)
                                newSplits.Add(split);
                            }
                            continue;
                        }

                        //Update registered consolidators for this symbol index
                        try
                        {
                            for (var j = 0; j < config.Consolidators.Count; j++)
                            {
                                config.Consolidators[j].Update(dataPoint);
                            }
                        }
                        catch (Exception err)
                        {
                            algorithm.RunTimeError = err;
                            _algorithmState = AlgorithmStatus.RuntimeError;
                            Log.Error("AlgorithmManager.Run(): RuntimeError: Consolidators update: " + err.Message);
                            return;
                        }

                        // TRADEBAR -- add to our dictionary
                        if (dataPoint.DataType == MarketDataType.TradeBar)
                        {
                            var bar = dataPoint as TradeBar;
                            if (bar != null)
                            {
                                newBars[bar.Symbol] = bar;
                                continue;
                            }
                        }

                        // TICK -- add to our dictionary
                        if (dataPoint.DataType == MarketDataType.Tick)
                        {
                            var tick = dataPoint as Tick;
                            if (tick != null)
                            {
                                List<Tick> ticks;
                                if (!newTicks.TryGetValue(tick.Symbol, out ticks))
                                {
                                    ticks = new List<Tick>(3);
                                    newTicks.Add(tick.Symbol, ticks);
                                }
                                ticks.Add(tick);
                                continue;
                            }
                        }

                        // if it was nothing else then it must be custom data

                        // CUSTOM DATA -- invoke on data method
                        //Send data into the generic algorithm event handlers
                        try
                        {
                            MethodInvoker methodInvoker;
                            if (methodInvokers.TryGetValue(config.Type, out methodInvoker))
                            {
                                methodInvoker(algorithm, dataPoint);
                            }
                        }
                        catch (Exception err)
                        {
                            algorithm.RunTimeError = err;
                            _algorithmState = AlgorithmStatus.RuntimeError;
                            Log.Error("AlgorithmManager.Run(): RuntimeError: Custom Data: " + err.Message + " STACK >>> " + err.StackTrace);
                            return;
                        }
                    }
                }

                try
                {
                    // fire off the dividend and split events before pricing events
                    if (hasOnDataDividends && newDividends.Count != 0)
                    {
                        methodInvokers[typeof (Dividends)](algorithm, newDividends);
                    }
                    if (hasOnDataSplits && newSplits.Count != 0)
                    {
                        methodInvokers[typeof (Splits)](algorithm, newSplits);
                    }
                }
                catch (Exception err)
                {
                    algorithm.RunTimeError = err;
                    _algorithmState = AlgorithmStatus.RuntimeError;
                    Log.Error("AlgorithmManager.Run(): RuntimeError: Dividends/Splits: " + err.Message + " STACK >>> " + err.StackTrace);
                    return;
                }

                //After we've fired all other events in this second, fire the pricing events:
                try
                {
                    if (hasOnDataTradeBars && newBars.Count > 0) methodInvokers[typeof (TradeBars)](algorithm, newBars);
                    if (hasOnDataTicks && newTicks.Count > 0) methodInvokers[typeof (Ticks)](algorithm, newTicks);
                }
                catch (Exception err)
                {
                    algorithm.RunTimeError = err;
                    _algorithmState = AlgorithmStatus.RuntimeError;
                    Log.Error("AlgorithmManager.Run(): RuntimeError: New Style Mode: " + err.Message + " STACK >>> " + err.StackTrace);
                    return;
                }

                // EVENT HANDLER v3.0 -- all data in a single event
                var slice = new Slice(time, newData.Values.SelectMany(x => x),
                    newBars.Count == 0 ? null : newBars,
                    newTicks.Count == 0 ? null : newTicks,
                    newSplits.Count == 0 ? null : newSplits,
                    newDividends.Count == 0 ? null : newDividends
                    );

                algorithm.OnData(slice);

                //If its the historical/paper trading models, wait until market orders have been "filled"
                // Manually trigger the event handler to prevent thread switch.
                transactions.ProcessSynchronousEvents();

                //Save the previous time for the sample calculations
                _previousTime = time;

                // Process any required events of the results handler such as sampling assets, equity, or stock prices.
                results.ProcessSynchronousEvents();
            } // End of ForEach feed.Bridge.GetConsumingEnumerable

            // stop timing the loops
            _currentTimeStepTime = DateTime.MinValue;

            //Stream over:: Send the final packet and fire final events:
            Log.Trace("AlgorithmManager.Run(): Firing On End Of Algorithm...");
            try
            {
                algorithm.OnEndOfAlgorithm();
            }
            catch (Exception err)
            {
                _algorithmState = AlgorithmStatus.RuntimeError;
                algorithm.RunTimeError = new Exception("Error running OnEndOfAlgorithm(): " + err.Message, err.InnerException);
                Log.Error("AlgorithmManager.OnEndOfAlgorithm(): " + err.Message + " STACK >>> " + err.StackTrace);
                return;
            }

            // Process any required events of the results handler such as sampling assets, equity, or stock prices.
            results.ProcessSynchronousEvents(forceProcess: true);

            //Liquidate Holdings for Calculations:
            if (_algorithmState == AlgorithmStatus.Liquidated && _liveMode)
            {
                Log.Trace("AlgorithmManager.Run(): Liquidating algorithm holdings...");
                algorithm.Liquidate();
                results.LogMessage("Algorithm Liquidated");
                results.SendStatusUpdate(job.AlgorithmId, AlgorithmStatus.Liquidated);
            }

            //Manually stopped the algorithm
            if (_algorithmState == AlgorithmStatus.Stopped)
            {
                Log.Trace("AlgorithmManager.Run(): Stopping algorithm...");
                results.LogMessage("Algorithm Stopped");
                results.SendStatusUpdate(job.AlgorithmId, AlgorithmStatus.Stopped);
            }

            //Backtest deleted.
            if (_algorithmState == AlgorithmStatus.Deleted)
            {
                Log.Trace("AlgorithmManager.Run(): Deleting algorithm...");
                results.DebugMessage("Algorithm Id:(" + job.AlgorithmId + ") Deleted by request.");
                results.SendStatusUpdate(job.AlgorithmId, AlgorithmStatus.Deleted);
            }

            //Algorithm finished, send regardless of commands:
            results.SendStatusUpdate(job.AlgorithmId, AlgorithmStatus.Completed);

            //Take final samples:
            results.SampleRange(algorithm.GetChartUpdates());
            results.SampleEquity(_previousTime, Math.Round(algorithm.Portfolio.TotalPortfolioValue, 4));
            results.SamplePerformance(_previousTime, Math.Round((algorithm.Portfolio.TotalPortfolioValue - startingPortfolioValue) * 100 / startingPortfolioValue, 10));
        } // End of Run();

        /// <summary>
        /// Set the quit state.
        /// </summary>
        public void SetStatus(AlgorithmStatus state)
        {
            lock (_lock)
            {
                //We don't want anyone elseto set our internal state to "Running". 
                //This is controlled by the algorithm private variable only.
                if (state != AlgorithmStatus.Running)
                {
                    _algorithmState = state;
                }
            }
        }

        /// <summary>
        /// Adds a method invoker if the method exists to the method invokers dictionary
        /// </summary>
        /// <typeparam name="T">The data type to check for 'OnData(T data)</typeparam>
        /// <param name="algorithm">The algorithm instance</param>
        /// <param name="methodInvokers">The dictionary of method invokers</param>
        /// <param name="methodName">The name of the method to search for</param>
        /// <returns>True if the method existed and was added to the collection</returns>
        private bool AddMethodInvoker<T>(IAlgorithm algorithm, Dictionary<Type, MethodInvoker> methodInvokers, string methodName = "OnData")
        {
            var newSplitMethodInfo = algorithm.GetType().GetMethod(methodName, new[] {typeof (T)});
            if (newSplitMethodInfo != null)
            {
                methodInvokers.Add(typeof(T), newSplitMethodInfo.DelegateForCallMethod());
                return true;
            }
            return false;
        }
    } // End of AlgorithmManager

} // End of Namespace.
