﻿using System.Linq;
using EPiServer.Commerce.Order;
using EPiServer.Core;
using EPiServer.Editor;
using EPiServer.Reference.Commerce.Site.Features.AddressBook.Services;
using EPiServer.Reference.Commerce.Site.Features.Checkout.Pages;
using EPiServer.Reference.Commerce.Site.Features.Checkout.Services;
using EPiServer.Reference.Commerce.Site.Infrastructure.Facades;
using EPiServer.Web.Mvc.Html;
using System.Web.Mvc;
using EPiServer.Globalization;
using Klarna.Checkout;
using Mediachase.Commerce.Orders.Managers;

namespace EPiServer.Reference.Commerce.Site.Features.Checkout.Controllers
{
    public class OrderConfirmationController : OrderConfirmationControllerBase<OrderConfirmationPage>
    {
        private readonly IKlarnaCheckoutService _klarnaCheckoutService;

        public OrderConfirmationController(
            ConfirmationService confirmationService,
            AddressBookService addressBookService,
            CustomerContextFacade customerContextFacade,
            IOrderGroupTotalsCalculator orderGroupTotalsCalculator,
            IKlarnaCheckoutService klarnaCheckoutService)
            : base(confirmationService, addressBookService, customerContextFacade, orderGroupTotalsCalculator)
        {
            _klarnaCheckoutService = klarnaCheckoutService;
        }

        [HttpGet]
        public ActionResult Index(OrderConfirmationPage currentPage, string notificationMessage, int? orderNumber, string trackingNumber)
        {
            IPurchaseOrder order = null;
            if (PageEditing.PageIsInEditMode)
            {
                order = _confirmationService.CreateFakePurchaseOrder();
            }
            else if (orderNumber.HasValue)
            {
                order = _confirmationService.GetOrder(orderNumber.Value);
            }
            else if (!string.IsNullOrEmpty(trackingNumber))
            {
                order = _confirmationService.GetByTrackingNumber(trackingNumber);
            }

            if (order != null/* && order.CustomerId == _customerContext.CurrentContactId*/)
            {
                var viewModel = CreateViewModel(currentPage, order);
                viewModel.NotificationMessage = notificationMessage;

                var paymentMethod = PaymentManager
                    .GetPaymentMethodBySystemName(Constants.KlarnaCheckoutSystemKeyword,
                        ContentLanguage.PreferredCulture.Name)
                    .PaymentMethod.FirstOrDefault();

                if (paymentMethod != null &&
                    order.GetFirstForm().Payments.Any(x => x.PaymentMethodId == paymentMethod.PaymentMethodId &&
                    !string.IsNullOrEmpty(order.Properties[Klarna.Common.Constants.KlarnaOrderIdField]?.ToString())))
                {
                    var klarnaOrder =
                        _klarnaCheckoutService.GetOrder(
                            order.Properties[Klarna.Common.Constants.KlarnaOrderIdField].ToString(), order.Market);
                    viewModel.KlarnaCheckoutHtmlSnippet = klarnaOrder.HtmlSnippet;
                    viewModel.IsKlarnaCheckout = true;
                }

                return View(viewModel);
            }

            return Redirect(Url.ContentUrl(ContentReference.StartPage));
        }
    }
}