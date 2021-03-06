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
*/

using System;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;

namespace QuantConnect.Tests.Common.Securities.Equity
{
    [TestFixture]
    public class EquityTransactionModelTests
    {
        private static readonly DateTime Noon = new DateTime(2014, 6, 24, 12, 0, 0);
        private static readonly TimeKeeper TimeKeeper = new TimeKeeper(Noon.ConvertToUtc(TimeZones.NewYork), new[] {TimeZones.NewYork});
        private const string Symbol = "SPY";

        [Test]
        public void PerformsMarketFillBuy()
        {
            var model = new EquityTransactionModel();
            var order = new MarketOrder(Symbol, 100, Noon, type: SecurityType.Equity);
            var config = CreateTradeBarConfig(Symbol);
            var security = new Security(SecurityExchangeHoursTests.CreateUsEquitySecurityExchangeHours(), config, 1);
            security.SetLocalTimeKeeper(TimeKeeper.GetLocalTimeKeeper(TimeZones.NewYork));
            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 101.123m));

            var fill = model.MarketFill(security, order);
            Assert.AreEqual(order.Quantity, fill.FillQuantity);
            Assert.AreEqual(security.Price, fill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, fill.Status);
            Assert.AreEqual(OrderStatus.Filled, order.Status);
        }
        [Test]
        public void PerformsMarketFillSell()
        {
            var model = new EquityTransactionModel();
            var order = new MarketOrder(Symbol, -100, Noon, type: SecurityType.Equity);
            var config = CreateTradeBarConfig(Symbol);
            var security = new Security(SecurityExchangeHoursTests.CreateUsEquitySecurityExchangeHours(), config, 1);
            security.SetLocalTimeKeeper(TimeKeeper.GetLocalTimeKeeper(TimeZones.NewYork));
            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 101.123m));

            var fill = model.MarketFill(security, order);
            Assert.AreEqual(order.Quantity, fill.FillQuantity);
            Assert.AreEqual(security.Price, fill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, fill.Status);
            Assert.AreEqual(OrderStatus.Filled, order.Status);
        }

        [Test]
        public void PerformsLimitFillBuy()
        {
            var model = new EquityTransactionModel();
            var order = new LimitOrder(Symbol, 100, 101.5m, Noon, type: SecurityType.Equity);
            var config = CreateTradeBarConfig(Symbol);
            var security = new Security(SecurityExchangeHoursTests.CreateUsEquitySecurityExchangeHours(), config, 1);
            security.SetLocalTimeKeeper(TimeKeeper.GetLocalTimeKeeper(TimeZones.NewYork));
            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 102m));

            var fill = model.LimitFill(security, order);

            Assert.AreEqual(0, fill.FillQuantity);
            Assert.AreEqual(0, fill.FillPrice);
            Assert.AreEqual(OrderStatus.None, fill.Status);
            Assert.AreEqual(OrderStatus.None, order.Status);

            security.SetMarketPrice(new TradeBar(Noon, Symbol, 102m, 103m, 101m, 102.3m, 100));

            fill = model.LimitFill(security, order);

            // this fills worst case scenario, so it's at the limit price
            Assert.AreEqual(order.Quantity, fill.FillQuantity);
            Assert.AreEqual(Math.Min(order.LimitPrice, security.High), fill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, fill.Status);
            Assert.AreEqual(OrderStatus.Filled, order.Status);
        }

        [Test]
        public void PerformsLimitFillSell()
        {
            var model = new EquityTransactionModel();
            var order = new LimitOrder(Symbol, -100, 101.5m, Noon, type: SecurityType.Equity);
            var config = CreateTradeBarConfig(Symbol);
            var security = new Security(SecurityExchangeHoursTests.CreateUsEquitySecurityExchangeHours(), config, 1);
            security.SetLocalTimeKeeper(TimeKeeper.GetLocalTimeKeeper(TimeZones.NewYork));
            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 101m));

            var fill = model.LimitFill(security, order);

            Assert.AreEqual(0, fill.FillQuantity);
            Assert.AreEqual(0, fill.FillPrice);
            Assert.AreEqual(OrderStatus.None, fill.Status);
            Assert.AreEqual(OrderStatus.None, order.Status);

            security.SetMarketPrice(new TradeBar(Noon, Symbol, 102m, 103m, 101m, 102.3m, 100));

            fill = model.LimitFill(security, order);

            // this fills worst case scenario, so it's at the limit price
            Assert.AreEqual(order.Quantity, fill.FillQuantity);
            Assert.AreEqual(Math.Max(order.LimitPrice, security.Low), fill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, fill.Status);
            Assert.AreEqual(OrderStatus.Filled, order.Status);
        }

        [Test]
        public void PerformsStopLimitFillBuy()
        {
            var model = new EquityTransactionModel();
            var order = new StopLimitOrder(Symbol, 100, 101.5m, 101.75m, Noon, type: SecurityType.Equity);
            var config = CreateTradeBarConfig(Symbol);
            var security = new Security(SecurityExchangeHoursTests.CreateUsEquitySecurityExchangeHours(), config, 1);
            security.SetLocalTimeKeeper(TimeKeeper.GetLocalTimeKeeper(TimeZones.NewYork));
            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 100m));

            var fill = model.StopLimitFill(security, order);

            Assert.AreEqual(0, fill.FillQuantity);
            Assert.AreEqual(0, fill.FillPrice);
            Assert.AreEqual(OrderStatus.None, fill.Status);
            Assert.AreEqual(OrderStatus.None, order.Status);

            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 102m));

            fill = model.StopLimitFill(security, order);

            Assert.AreEqual(0, fill.FillQuantity);
            Assert.AreEqual(0, fill.FillPrice);
            Assert.AreEqual(OrderStatus.None, fill.Status);
            Assert.AreEqual(OrderStatus.None, order.Status);

            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 101.66m));

            fill = model.StopLimitFill(security, order);

            // this fills worst case scenario, so it's at the limit price
            Assert.AreEqual(order.Quantity, fill.FillQuantity);
            Assert.AreEqual(order.LimitPrice, fill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, fill.Status);
            Assert.AreEqual(OrderStatus.Filled, order.Status);
        }

        [Test]
        public void PerformsStopLimitFillSell()
        {
            var model = new EquityTransactionModel();
            var order = new StopLimitOrder(Symbol, -100, 101.75m, 101.50m, Noon, type: SecurityType.Equity);
            var config = CreateTradeBarConfig(Symbol);
            var security = new Security(SecurityExchangeHoursTests.CreateUsEquitySecurityExchangeHours(), config, 1);
            security.SetLocalTimeKeeper(TimeKeeper.GetLocalTimeKeeper(TimeZones.NewYork));
            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 102m));

            var fill = model.StopLimitFill(security, order);

            Assert.AreEqual(0, fill.FillQuantity);
            Assert.AreEqual(0, fill.FillPrice);
            Assert.AreEqual(OrderStatus.None, fill.Status);
            Assert.AreEqual(OrderStatus.None, order.Status);

            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 101m));

            fill = model.StopLimitFill(security, order);

            Assert.AreEqual(0, fill.FillQuantity);
            Assert.AreEqual(0, fill.FillPrice);
            Assert.AreEqual(OrderStatus.None, fill.Status);
            Assert.AreEqual(OrderStatus.None, order.Status);

            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 101.66m));

            fill = model.StopLimitFill(security, order);

            // this fills worst case scenario, so it's at the limit price
            Assert.AreEqual(order.Quantity, fill.FillQuantity);
            Assert.AreEqual(order.LimitPrice, fill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, fill.Status);
            Assert.AreEqual(OrderStatus.Filled, order.Status);
        }

        [Test]
        public void PerformsStopMarketFillBuy()
        {
            var model = new EquityTransactionModel();
            var order = new StopMarketOrder(Symbol, 100, 101.5m, Noon, type: SecurityType.Equity);
            var config = CreateTradeBarConfig(Symbol);
            var security = new Security(SecurityExchangeHoursTests.CreateUsEquitySecurityExchangeHours(), config, 1);
            security.SetLocalTimeKeeper(TimeKeeper.GetLocalTimeKeeper(TimeZones.NewYork));
            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 101m));

            var fill = model.StopMarketFill(security, order);

            Assert.AreEqual(0, fill.FillQuantity);
            Assert.AreEqual(0, fill.FillPrice);
            Assert.AreEqual(OrderStatus.None, fill.Status);
            Assert.AreEqual(OrderStatus.None, order.Status);

            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 102.5m));

            fill = model.StopMarketFill(security, order);

            // this fills worst case scenario, so it's min of asset/stop price
            Assert.AreEqual(order.Quantity, fill.FillQuantity);
            Assert.AreEqual(Math.Max(security.Price, order.StopPrice), fill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, fill.Status);
            Assert.AreEqual(OrderStatus.Filled, order.Status);
        }

        [Test]
        public void PerformsStopMarketFillSell()
        {
            var model = new EquityTransactionModel();
            var order = new StopMarketOrder(Symbol, -100, 101.5m, Noon, type: SecurityType.Equity);
            var config = CreateTradeBarConfig(Symbol);
            var security = new Security(SecurityExchangeHoursTests.CreateUsEquitySecurityExchangeHours(), config, 1);
            security.SetLocalTimeKeeper(TimeKeeper.GetLocalTimeKeeper(TimeZones.NewYork));
            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 102m));

            var fill = model.StopMarketFill(security, order);

            Assert.AreEqual(0, fill.FillQuantity);
            Assert.AreEqual(0, fill.FillPrice);
            Assert.AreEqual(OrderStatus.None, fill.Status);
            Assert.AreEqual(OrderStatus.None, order.Status);

            security.SetMarketPrice(new IndicatorDataPoint(Symbol, Noon, 101m));

            fill = model.StopMarketFill(security, order);

            // this fills worst case scenario, so it's min of asset/stop price
            Assert.AreEqual(order.Quantity, fill.FillQuantity);
            Assert.AreEqual(Math.Min(security.Price, order.StopPrice), fill.FillPrice);
            Assert.AreEqual(OrderStatus.Filled, fill.Status);
            Assert.AreEqual(OrderStatus.Filled, order.Status);
        }
        private SubscriptionDataConfig CreateTradeBarConfig(string symbol)
        {
            return new SubscriptionDataConfig(typeof(TradeBar), SecurityType.Equity, symbol, Resolution.Minute, "usa", TimeZones.NewYork, true, true, true, true, false);
        }
    }
}
