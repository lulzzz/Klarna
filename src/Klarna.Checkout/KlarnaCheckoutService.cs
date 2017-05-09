﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using EPiServer;
using EPiServer.Commerce.Order;
using EPiServer.Globalization;
using EPiServer.ServiceLocation;
using EPiServer.Logging;
using EPiServer.Web.Routing;
using Klarna.Checkout.Models;
using Klarna.Common;
using Klarna.Common.Extensions;
using Klarna.Common.Helpers;
using Klarna.Rest;
using Klarna.Rest.Models;
using Klarna.Rest.Transport;
using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Orders;
using Mediachase.Commerce.Orders.Managers;
using Mediachase.Commerce.Orders.Search;

namespace Klarna.Checkout
{
    public abstract class KlarnaService
    {
    }

    [ServiceConfiguration(typeof(IKlarnaCheckoutService))]
    public class KlarnaCheckoutService : KlarnaService, IKlarnaCheckoutService
    {
        private readonly ILogger _logger = LogManager.GetLogger(typeof(KlarnaCheckoutService));
        private readonly IOrderGroupTotalsCalculator _orderGroupTotalsCalculator;

        private readonly IOrderRepository _orderRepository;
        private readonly ReferenceConverter _referenceConverter;
        private readonly UrlResolver _urlResolver;
        private readonly IContentRepository _contentRepository;
        private readonly IOrderNumberGenerator _orderNumberGenerator;
        private readonly IPaymentProcessor _paymentProcessor;
        private readonly IOrderGroupCalculator _orderGroupCalculator;
        private readonly IConnectionFactory _connectionFactory;

        private Client _client;

        public KlarnaCheckoutService(
            IOrderGroupTotalsCalculator orderGroupTotalsCalculator,
            IOrderRepository orderRepository,
            ReferenceConverter referenceConverter,
            UrlResolver urlResolver,
            IContentRepository contentRepository,
            IOrderNumberGenerator orderNumberGenerator,
            IPaymentProcessor paymentProcessor,
            IOrderGroupCalculator orderGroupCalculator,
            IConnectionFactory connectionFactory)
        {
            _orderGroupTotalsCalculator = orderGroupTotalsCalculator;
            _orderRepository = orderRepository;
            _referenceConverter = referenceConverter;
            _urlResolver = urlResolver;
            _contentRepository = contentRepository;
            _orderNumberGenerator = orderNumberGenerator;
            _paymentProcessor = paymentProcessor;
            _orderGroupCalculator = orderGroupCalculator;
            _connectionFactory = connectionFactory;
        }

        public Client Client
        {
            get
            {
                if (_client == null)
                {
                    var paymentMethod =
                        PaymentManager.GetPaymentMethodBySystemName(Constants.KlarnaCheckoutSystemKeyword,
                            ContentLanguage.PreferredCulture.Name);
                    if (paymentMethod != null)
                    {
                        var connectionConfiguration = _connectionFactory.GetConnectionConfiguration(paymentMethod);
                        var connector = ConnectorFactory.Create(connectionConfiguration.Username,
                            connectionConfiguration.Password, new Uri(connectionConfiguration.ApiUrl));

                        _client = new Client(connector);
                    }
                }
                return _client;
            }
        }

        public CheckoutOrderData CreateOrUpdateOrder(ICart cart)
        {
            var orderId = cart.Properties[Constants.KlarnaCheckoutOrderIdField]?.ToString();
            if (string.IsNullOrWhiteSpace(orderId))
            {
                return CreateOrder(cart);
            }
            else
            {
                return UpdateOrder(orderId, cart);
            }
        }

        public CheckoutOrderData CreateOrder(ICart cart)
        {
            var checkout = Client.NewCheckoutOrder();

            var lines = GetOrderLines(cart);

            var merchantUrls = new MerchantUrls
            {
                Terms = new Uri("http://www.merchant.com/toc"),
                Checkout = new Uri("http://www.merchant.com/checkout?klarna_order_id={checkout.order.id}"),
                Confirmation = new Uri("http://www.merchant.com/thank-you?klarna_order_id={checkout.order.id}"),
                Push = new Uri("http://www.merchant.com/create_order?klarna_order_id={checkout.order.id}")
            };
            
            var totals = _orderGroupTotalsCalculator.GetTotals(cart);

            var orderData = new CheckoutOrderData()
            {
                PurchaseCountry = CountryCodeHelper.GetTwoLetterCountryCode(cart.Market.Countries.FirstOrDefault()),
                PurchaseCurrency = cart.Currency.CurrencyCode,
                Locale = ContentLanguage.PreferredCulture.Name,
                OrderAmount = AmountHelper.GetAmount(totals.Total),
                OrderTaxAmount = AmountHelper.GetAmount(totals.TaxTotal),
                OrderLines = lines,
                MerchantUrls = merchantUrls
            };

            try
            {
                checkout.Create(orderData);
                orderData = checkout.Fetch();

                // Store checkout order id on cart
                cart.Properties[Constants.KlarnaCheckoutOrderIdField] = orderData.OrderId;
                _orderRepository.Save(cart);

                return orderData;
            }
            catch (ApiException ex)
            {
                Console.WriteLine(ex.ErrorMessage.ErrorCode);
                Console.WriteLine(ex.ErrorMessage.ErrorMessages);
                Console.WriteLine(ex.ErrorMessage.CorrelationId);
            }
            catch (WebException ex)
            {
                Console.WriteLine(ex.Message);
            }

            return null;
        }

        public CheckoutOrderData UpdateOrder(string orderId, ICart cart)
        {
            var checkout = Client.NewCheckoutOrder(orderId);
            var totals = _orderGroupTotalsCalculator.GetTotals(cart);

            var orderData = new PatchedCheckoutOrderData
            {
                OrderAmount = AmountHelper.GetAmount(totals.Total),
                OrderTaxAmount = AmountHelper.GetAmount(totals.TaxTotal),
                ShippingOptions = GetShippingOptions(cart)
            } as CheckoutOrderData;
            

            var lines = GetOrderLines(cart);
            
            orderData.OrderLines = lines;
            
            try
            {
                orderData = checkout.Update(orderData);
                return orderData;
            }
            catch (ApiException ex)
            {
                Console.WriteLine(ex.ErrorMessage.ErrorCode);
                Console.WriteLine(ex.ErrorMessage.ErrorMessages);
                Console.WriteLine(ex.ErrorMessage.CorrelationId);
            }
            catch (WebException ex)
            {
                Console.WriteLine(ex.Message);
            }

            return null;
        }

        public CheckoutOrderData GetOrder(string orderId)
        {
            var checkout = Client.NewCheckoutOrder(orderId);

            return checkout.Fetch();
        }

        public ICart GetCartByKlarnaOrderId(string orderId)
        {
            var checkoutOrderData = GetOrder(orderId);

            var cart = GetCart(orderId);

            //TODO: compare checkoutOrderData with cart

            return cart;
        }

        private IEnumerable<ShippingOption> GetShippingOptions(ICart cart)
        {
            var methods = ShippingManager.GetShippingMethodsByMarket(cart.Market.MarketId.Value, false);
            return methods.ShippingMethod.Select(method => new ShippingOption
            {
                Id = method.ShippingMethodId.ToString(),
                Name = method.DisplayName,
                Price = AmountHelper.GetAmount(method.BasePrice),
                PreSelected = method.IsDefault,
                TaxAmount = 1,
                TaxRate = 1,
                Description = method.Description
            });
        }

        private List<OrderLine> GetOrderLines(ICart cart)
        {
            // Only add order lines, Klarna adds shipping costs
            // https://developers.klarna.com/en/gb/kco-v3/checkout/additional-features/tax-shipping

            var orderLines = new List<OrderLine>();
            foreach (var lineItem in cart.GetAllLineItems())
            {
                var orderLine = lineItem.GetOrderLine(cart.Currency);
                orderLines.Add(orderLine);
            }
            
            return orderLines;
        }

        private ICart GetCart(string orderId)
        {
            OrderSearchOptions searchOptions = new OrderSearchOptions();
            searchOptions.CacheResults = false;
            searchOptions.StartingRecord = 0;
            searchOptions.RecordsToRetrieve = 1;
            searchOptions.Classes = new System.Collections.Specialized.StringCollection { "ShoppingCart" };
            searchOptions.Namespace = "Mediachase.Commerce.Orders";

            var parameters = new OrderSearchParameters();
            parameters.SqlMetaWhereClause = $"META.{Common.Constants.KlarnaOrderIdField} LIKE '{orderId}'";

            var cart = OrderContext.Current.FindCarts(parameters, searchOptions)?.FirstOrDefault();

            if (cart != null)
            {
                return _orderRepository.LoadCart<ICart>(cart.CustomerId, "Default");
            }
            return null;
        }
    }
}