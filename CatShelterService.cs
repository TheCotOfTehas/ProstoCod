using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microservices.Common.Exceptions;
using Microservices.ExternalServices.Authorization;
using Microservices.ExternalServices.Authorization.Types;
using Microservices.ExternalServices.Billing;
using Microservices.ExternalServices.Billing.Types;
using Microservices.ExternalServices.CatDb;
using Microservices.ExternalServices.CatDb.Types;
using Microservices.ExternalServices.CatExchange;
using Microservices.ExternalServices.CatExchange.Types;
using Microservices.ExternalServices.Database;
using Microservices.Types;

namespace Microservices
{
    public class CatShelterService : ICatShelterService
    {
        private IDatabase Database { get; set; }
        private IAuthorizationService AuthorizationService { get; set; }
        private IBillingService BillingService { get; set; }
        private ICatInfoService CatInfoService { get; set; }
        private ICatExchangeService CatExchangeService { get; set; }
        private IDatabaseCollection<CatDocument, Guid> DatabaseCat { get; set; }
        public CatShelterService(
            IDatabase database,
            IAuthorizationService authorizationService,
            IBillingService billingService,
            ICatInfoService catInfoService,
            ICatExchangeService catExchangeService)
        {
            Database = database;
            AuthorizationService = authorizationService;                
            BillingService = billingService;
            CatInfoService = catInfoService;
            CatExchangeService = catExchangeService;
            DatabaseCat =  Database.GetCollection<CatDocument, Guid>("БазаДанныхПриюта");
        }

        public async Task<List<Cat>> GetCatsAsync(string sessionId, int skip, int limit, CancellationToken cancellationToken)
        {
            //throw new Exception("Запущен метод GetCatsAsync");
            var authorizationResult = await Task.Run(() => AuthorizationService.AuthorizeAsync(sessionId, cancellationToken));
            if (!authorizationResult.IsSuccess)
                throw new AuthorizationException();

            var resultCatDocument = await Task.Run(() => DatabaseCat
                .FindAsync(cat => true, cancellationToken));

            return  resultCatDocument
                .Select(catDocument => (Cat)catDocument)
                .ToList();
        }

        public async Task AddCatToFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            //throw new Exception("Запущен метод AddCatToFavouritesAsync");

            var authorizationResult = await Task.Run(() => AuthorizationService.AuthorizeAsync(sessionId, cancellationToken));
            if (!authorizationResult.IsSuccess)
                throw new AuthorizationException();

            var listCat = await Task.Run(() => DatabaseCat.FindAsync(cat => cat.Id == catId, cancellationToken));

            await Task.Run(() => listCat
                .Where(cat => cat.Id == catId)
                .First()
                .isFavarit = true);
        }

        public async Task<List<Cat>> GetFavouriteCatsAsync(string sessionId, CancellationToken cancellationToken)
        {
            //throw new Exception("Запущен метод GetFavouriteCatsAsync");

            var authorizationResult = await Task.Run(() => AuthorizationService.AuthorizeAsync(sessionId, cancellationToken));
            if (!authorizationResult.IsSuccess)
                throw new AuthorizationException();

            var listCat = await Task.Run(() => DatabaseCat.FindAsync(cat => cat.isFavarit, cancellationToken));

            return await Task.Run(() => listCat.Select(cat => (Cat)cat).ToList());
        }

        public async Task DeleteCatFromFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            //throw new Exception("Запущен метод DeleteCatFromFavouritesAsync");

            var authorizationResult = await Task.Run(() => AuthorizationService.AuthorizeAsync(sessionId, cancellationToken));
            if (!authorizationResult.IsSuccess)
                throw new AuthorizationException();

            var listCat = await Task.Run(() => DatabaseCat.FindAsync(cat => cat.Id == catId, cancellationToken));

            await Task.Run(() => listCat
                .Where(cat => cat.Id == catId)
                .First()
                .isFavarit = false);
        }

        public async Task<Bill> BuyCatAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            //throw new Exception("Запущен метод BuyCatAsync");
            var authorizationResult = await Task.Run(() => AuthorizationService.AuthorizeAsync(sessionId, cancellationToken));
            if (!authorizationResult.IsSuccess)
                throw new AuthorizationException();

            var listCatDocument = await Task.Run(() => DatabaseCat.FindAsync(cat => cat.Id == catId, cancellationToken));

            var cat = listCatDocument.FirstOrDefault();
            var product = new Product { BreedId = cat.BreedId, Id = cat.Id };

            var result = await Task.Run(() =>
            {
                BillingService.AddProductAsync(product, cancellationToken);
                Task.Run(() => DatabaseCat.DeleteAsync(cat.Id, cancellationToken));
                return BillingService.SellProductAsync(product.Id, cat.Price, cancellationToken);
            });

            return result == null ? result : throw new InvalidRequestException();
        }

        public async Task<Guid> AddCatAsync(string sessionId, AddCatRequest request, CancellationToken cancellationToken)
        {
            //throw new Exception("Запущен метод AddCatAsync");
            var authorizationResult = await Task.Run(() => AuthorizationService.AuthorizeAsync(sessionId, cancellationToken));
            if (!authorizationResult.IsSuccess)
                throw new AuthorizationException();

            var catInfo = await Task.Run(() => CatInfoService.FindByBreedNameAsync(request.Breed, cancellationToken));
            var catPriceHistory = await Task.Run(() => CatExchangeService.GetPriceInfoAsync(catInfo.BreedId ,cancellationToken));

            var catDocument = new CatDocument(authorizationResult.UserId);
            catDocument.AddPrice(catPriceHistory);
            catDocument.AddCatInfo(catInfo);
            catDocument.AddRequest(request);

            await Task.Run(() => DatabaseCat.WriteAsync(catDocument ,cancellationToken));
            return catDocument.Id;
        }
    }

    internal class CatDocument : Cat, IEntityWithId<Guid>
    {
        public bool isFavarit { get; set; }
        public CatDocument(Guid userId)
        {
            this.Id = Guid.NewGuid();
            this.AddedBy = userId;
            isFavarit = false;
        }

        public void AddRequest(AddCatRequest request)
        {
            this.Name = request.Name;
            this.Breed = request.Breed;
            this.CatPhoto = request.Photo;
        }

        public void AddCatInfo(CatInfo catInfo)
        {
            this.BreedPhoto = catInfo.Photo;
            this.BreedId = catInfo.BreedId;
        }

        public void AddPrice(CatPriceHistory catPriceHistory)
        {
            var listCatPrice = catPriceHistory.Prices;
            decimal pricesLast = 1000;
            if (listCatPrice.Count != null && listCatPrice.Count > 0)
                pricesLast = listCatPrice.Last().Price;

            DateTime dataLast;
            if (listCatPrice.Count != null && listCatPrice.Count > 0)
                dataLast = listCatPrice.Last().Date;

            this.Price = pricesLast != null ? pricesLast : 1000;
            this.Prices = new List<(DateTime Date, decimal Price)>();

            foreach (var item in listCatPrice)
            {
                var oneBill = (item.Date, item.Price);
                Prices.Add(oneBill);
            }
            Prices.Reverse();
        }
    }
}