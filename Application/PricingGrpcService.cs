using System.Globalization;
using Grpc.Core;
using RecipeCost.Pricing.Application;
using RecipeCost.Pricing.Integrations;
using Rpc = Recipe.Pricing.V1;

namespace RecipeCost.Pricing.Services;

public sealed class PricingGrpcService(
    PricingCalculator calculator,
    UsdaFoodDataClient usdaClient) : Rpc.PricingService.PricingServiceBase
{
    public override Task<Rpc.CalculateRecipeCostResponse> CalculateRecipeCost(
        Rpc.CalculateRecipeCostRequest request,
        ServerCallContext context)
    {
        try
        {
            var lines = request.Lines.Select(MapLine).ToArray();
            var result = calculator.CalculateRecipeCost(lines, request.Servings);
            return Task.FromResult(MapResult(result));
        }
        catch (FormatException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
        catch (InvalidOperationException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
    }

    public override async Task<Rpc.SearchUsdaFoodsResponse> SearchUsdaFoods(
        Rpc.SearchUsdaFoodsRequest request,
        ServerCallContext context)
    {
        try
        {
            var foods = await usdaClient.SearchFoodsAsync(request.Query, context.CancellationToken);
            var response = new Rpc.SearchUsdaFoodsResponse();
            response.Foods.AddRange(foods.Select(food => new Rpc.UsdaFoodSearchResult
            {
                FdcId = food.FdcId,
                DataType = food.DataType,
                Description = food.Description,
                BrandOwner = food.BrandOwner ?? string.Empty,
            }));
            return response;
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
        catch (UsdaFoodDataException error)
        {
            throw new RpcException(new Status(MapStatus(error.StatusCode), error.Message));
        }
    }

    public override async Task<Rpc.GetUsdaDensitySuggestionsResponse> GetUsdaDensitySuggestions(
        Rpc.GetUsdaDensitySuggestionsRequest request,
        ServerCallContext context)
    {
        try
        {
            var suggestions = await usdaClient.GetDensitySuggestionsAsync(request.FdcId, context.CancellationToken);
            var response = new Rpc.GetUsdaDensitySuggestionsResponse();
            response.Suggestions.AddRange(suggestions.Select(item => new Rpc.UsdaDensitySuggestion
            {
                FdcId = item.FdcId,
                DataType = item.DataType,
                FoodDescription = item.FoodDescription,
                PortionId = item.PortionId,
                PortionAmount = FormatDecimal(item.PortionAmount),
                PortionDescription = item.PortionDescription,
                MeasureUnit = item.MeasureUnit,
                GramWeight = FormatDecimal(item.GramWeight),
                DensityGramsPerMl = FormatDecimal(item.DensityGramsPerMl),
            }));
            return response;
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
        catch (UsdaFoodDataException error)
        {
            throw new RpcException(new Status(MapStatus(error.StatusCode), error.Message));
        }
    }

    private static RecipeLineInput MapLine(Rpc.RecipeLineInput line)
    {
        var conversions = line.Conversions.Select(conversion => new UnitConversion(
            conversion.IngredientId,
            conversion.FromUnitId,
            conversion.ToUnitId,
            ParseDecimal(conversion.ConversionFactor, "conversion factor"))).ToArray();

        var candidates = line.Candidates.Select(candidate => new ProductCandidate(
            candidate.ProductId,
            candidate.ProductName,
            candidate.IngredientId,
            ParseDecimal(candidate.PackageQuantity, "package quantity"),
            MapUnit(candidate.PackageUnit),
            candidate.Price is not null ? MapPrice(candidate.Price) : null)).ToArray();

        decimal? density = line.HasDensityGramsPerMl
            ? ParseDecimal(line.DensityGramsPerMl, "density")
            : null;

        return new RecipeLineInput(
            line.RecipeIngredientId,
            line.IngredientId,
            ParseDecimal(line.Quantity, "recipe quantity"),
            MapUnit(line.Unit),
            density,
            candidates,
            conversions);
    }

    private static UnitDefinition MapUnit(Rpc.UnitInput unit) => new(
        unit.UnitId,
        unit.UnitType,
        ParseDecimal(unit.ConversionToBase, "unit conversion"));

    private static PriceObservation MapPrice(Rpc.PriceInput price)
    {
        DateTimeOffset? recordedAt = null;
        if (!string.IsNullOrWhiteSpace(price.RecordedAt))
        {
            if (!DateTimeOffset.TryParse(
                    price.RecordedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                throw new FormatException("Price recorded_at must be an ISO-8601 timestamp.");
            }
            recordedAt = parsed;
        }

        return new PriceObservation(
            price.PriceId,
            price.ProductId,
            ParseDecimal(price.Price, "price"),
            price.CurrencyCode,
            recordedAt,
            string.IsNullOrWhiteSpace(price.Source) ? null : price.Source);
    }

    private static Rpc.CalculateRecipeCostResponse MapResult(RecipeCostResult result)
    {
        var response = new Rpc.CalculateRecipeCostResponse
        {
            KnownSubtotal = PricingCalculator.FormatMoney(result.KnownSubtotal),
            TotalCost = result.TotalCost.HasValue
                ? PricingCalculator.FormatMoney(result.TotalCost.Value)
                : string.Empty,
            CurrencyCode = result.CurrencyCode ?? string.Empty,
            IsComplete = result.IsComplete,
            CostPerServing = result.CostPerServing.HasValue
                ? PricingCalculator.FormatMoney(result.CostPerServing.Value)
                : string.Empty,
        };

        response.Lines.AddRange(result.Lines.Select(line => new Rpc.IngredientCostResult
        {
            RecipeIngredientId = line.RecipeIngredientId,
            IngredientId = line.IngredientId,
            ProductId = line.ProductId ?? string.Empty,
            PriceId = line.PriceId ?? string.Empty,
            Cost = line.Cost.HasValue ? PricingCalculator.FormatMoney(line.Cost.Value) : string.Empty,
            CurrencyCode = line.CurrencyCode ?? string.Empty,
            PriceRecordedAt = line.PriceRecordedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            Source = line.Source ?? string.Empty,
            Issue = line.Issue ?? string.Empty,
            HasCost = line.Cost.HasValue,
        }));

        return response;
    }

    internal static decimal ParseDecimal(string value, string fieldName)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException($"{fieldName} must be a valid decimal value.");
        }
        return parsed;
    }

    internal static string FormatDecimal(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static StatusCode MapStatus(int statusCode) => statusCode switch
    {
        400 => StatusCode.InvalidArgument,
        404 => StatusCode.NotFound,
        503 => StatusCode.Unavailable,
        _ => StatusCode.Internal,
    };
}
