using System.Security.Cryptography;
using System.Text;
using RecipeCost.Pricing.Integrations;
using RecipeCost.Pricing.Services;

namespace RecipeCost.Pricing.Application;

public static class PricingServiceHttpApi
{
    public static void MapPricingServiceHttpApi(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        app.MapPost("/v1/pricing/calculate", (
            HttpContext context,
            RecipeCostRequest request,
            IConfiguration configuration,
            PricingCalculator calculator) =>
        {
            var authorizationFailure = CheckAuthorization(context, configuration);
            if (authorizationFailure is not null) return authorizationFailure;

            try
            {
                var result = calculator.CalculateRecipeCost(request.Lines, request.Servings);
                return Results.Ok(new
                {
                    knownSubtotal = PricingCalculator.FormatMoney(result.KnownSubtotal),
                    totalCost = result.TotalCost.HasValue
                        ? PricingCalculator.FormatMoney(result.TotalCost.Value)
                        : null,
                    costPerServing = result.CostPerServing.HasValue
                        ? PricingCalculator.FormatMoney(result.CostPerServing.Value)
                        : null,
                    currencyCode = result.CurrencyCode,
                    isComplete = result.IsComplete,
                    lines = result.Lines.Select(line => new
                    {
                        recipeIngredientId = line.RecipeIngredientId,
                        ingredientId = line.IngredientId,
                        productId = line.ProductId,
                        priceId = line.PriceId,
                        cost = line.Cost.HasValue ? PricingCalculator.FormatMoney(line.Cost.Value) : null,
                        currencyCode = line.CurrencyCode,
                        priceRecordedAt = line.PriceRecordedAt,
                        source = line.Source,
                        issue = line.Issue,
                    }),
                });
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { message = error.Message });
            }
            catch (InvalidOperationException error)
            {
                return Results.UnprocessableEntity(new { message = error.Message });
            }
        });

        app.MapPost("/v1/usda/foods/search", async (
            HttpContext context,
            SearchUsdaFoodsHttpRequest request,
            IConfiguration configuration,
            UsdaFoodDataClient usdaClient,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = CheckAuthorization(context, configuration);
            if (authorizationFailure is not null) return authorizationFailure;

            try
            {
                var foods = await usdaClient.SearchFoodsAsync(request.Query, cancellationToken);
                return Results.Ok(new { foods });
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { message = error.Message });
            }
            catch (UsdaFoodDataException error)
            {
                return Results.Json(new { message = error.Message }, statusCode: error.StatusCode);
            }
        });

        app.MapGet("/v1/usda/foods/{fdcId:int}/density-suggestions", async (
            HttpContext context,
            int fdcId,
            IConfiguration configuration,
            UsdaFoodDataClient usdaClient,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = CheckAuthorization(context, configuration);
            if (authorizationFailure is not null) return authorizationFailure;

            try
            {
                var suggestions = await usdaClient.GetDensitySuggestionsAsync(fdcId, cancellationToken);
                return Results.Ok(new
                {
                    suggestions = suggestions.Select(item => new
                    {
                        item.FdcId,
                        item.DataType,
                        item.FoodDescription,
                        item.PortionId,
                        portionAmount = PricingGrpcService.FormatDecimal(item.PortionAmount),
                        item.PortionDescription,
                        item.MeasureUnit,
                        gramWeight = PricingGrpcService.FormatDecimal(item.GramWeight),
                        densityGramsPerMl = PricingGrpcService.FormatDecimal(item.DensityGramsPerMl),
                    }),
                });
            }
            catch (ArgumentOutOfRangeException error)
            {
                return Results.BadRequest(new { message = error.Message });
            }
            catch (UsdaFoodDataException error)
            {
                return Results.Json(new { message = error.Message }, statusCode: error.StatusCode);
            }
        });
    }

    private static IResult? CheckAuthorization(HttpContext context, IConfiguration configuration)
    {
        var expected = configuration["PRICING_SERVICE_TOKEN"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            return Results.Problem(
                "Pricing service credentials are not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            ? authorization["Bearer ".Length..]
            : string.Empty;
        var actualBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes)
            ? null
            : Results.Unauthorized();
    }
}

public sealed record RecipeCostRequest(IReadOnlyList<RecipeLineInput> Lines, int Servings);
public sealed record SearchUsdaFoodsHttpRequest(string Query);
