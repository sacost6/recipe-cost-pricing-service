using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace RecipeCost.Pricing.Integrations;

public sealed record UsdaFoodSearchResult(
    int FdcId,
    string DataType,
    string Description,
    string? BrandOwner);

public sealed record UsdaDensitySuggestion(
    int FdcId,
    string DataType,
    string FoodDescription,
    int PortionId,
    decimal PortionAmount,
    string PortionDescription,
    string MeasureUnit,
    decimal GramWeight,
    decimal DensityGramsPerMl);

public sealed class UsdaFoodDataException(int statusCode, string stage, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Stage { get; } = stage;
}

public sealed class UsdaFoodDataClient(
    HttpClient httpClient,
    IConfiguration configuration,
    IMemoryCache cache)
{
    private static readonly string[] SupportedDataTypes =
    [
        "Foundation",
        "SR Legacy",
        "Survey (FNDDS)",
    ];

    private static readonly (string Name, decimal Milliliters)[] VolumeMeasures =
    [
        ("milliliter", 1m), ("millilitre", 1m), ("ml", 1m),
        ("milliliters", 1m), ("millilitres", 1m),
        ("liter", 1000m), ("liters", 1000m), ("litre", 1000m), ("litres", 1000m), ("l", 1000m),
        ("teaspoon", 4.928921594m), ("teaspoons", 4.928921594m), ("tsp", 4.928921594m),
        ("tablespoon", 14.786764781m), ("tablespoons", 14.786764781m), ("tbsp", 14.786764781m), ("tbs", 14.786764781m),
        ("fluid ounce", 29.573529563m), ("fluid ounces", 29.573529563m), ("fl oz", 29.573529563m),
        ("cup", 236.588236500m), ("cups", 236.588236500m),
        ("pint", 473.176473m), ("pints", 473.176473m), ("pt", 473.176473m),
        ("quart", 946.352946m), ("quarts", 946.352946m), ("qt", 946.352946m),
        ("gallon", 3785.411784m), ("gallons", 3785.411784m), ("gal", 3785.411784m),
    ];

    private readonly string? _apiKey = configuration["USDA_FDC_API_KEY"];

    public async Task<IReadOnlyList<UsdaFoodSearchResult>> SearchFoodsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length is < 2 or > 200)
        {
            throw new ArgumentException("Search query must contain 2 to 200 characters.", nameof(query));
        }

        var cacheKey = $"usda-food-search:{normalizedQuery.ToLowerInvariant()}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<UsdaFoodSearchResult>? cached) && cached is not null)
        {
            return cached;
        }

        var uri = $"foods/search?api_key={Uri.EscapeDataString(_apiKey!)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new
            {
                query = normalizedQuery,
                dataType = SupportedDataTypes,
                pageSize = 25,
            }),
        };

        using var response = await SendAsync(request, "food search", cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("foods", out var foods) || foods.ValueKind != JsonValueKind.Array)
        {
            throw new UsdaFoodDataException(502, "food search", "USDA returned an invalid search response.");
        }

        var results = foods.EnumerateArray()
            .Select(food => new UsdaFoodSearchResult(
                GetInt32(food, "fdcId"),
                GetString(food, "dataType") ?? string.Empty,
                GetString(food, "description") ?? string.Empty,
                GetString(food, "brandOwner")))
            .Where(food => food.FdcId > 0 && food.Description.Length > 0)
            .ToArray();

        cache.Set(cacheKey, results, TimeSpan.FromMinutes(30));
        return results;
    }

    public async Task<IReadOnlyList<UsdaDensitySuggestion>> GetDensitySuggestionsAsync(
        int fdcId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (fdcId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fdcId));
        }

        var cacheKey = $"usda-density-suggestions:{fdcId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<UsdaDensitySuggestion>? cached) && cached is not null)
        {
            return cached;
        }

        var uri = $"food/{fdcId}?format=full&api_key={Uri.EscapeDataString(_apiKey!)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, "food details", cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        var dataType = GetString(root, "dataType") ?? string.Empty;
        var description = GetString(root, "description") ?? string.Empty;
        var suggestions = new List<UsdaDensitySuggestion>();

        if (root.TryGetProperty("foodPortions", out var portions) && portions.ValueKind == JsonValueKind.Array)
        {
            foreach (var portion in portions.EnumerateArray())
            {
                var portionId = GetInt32(portion, "id");
                var amount = GetDecimal(portion, "amount");
                var gramWeight = GetDecimal(portion, "gramWeight");
                var portionDescription = GetString(portion, "portionDescription") ?? string.Empty;
                var measureUnit = GetMeasureUnit(portion);

                if (portionId <= 0 || amount <= 0 || gramWeight <= 0 ||
                    !TryGetMillilitersPerUnit(measureUnit, portionDescription, out var millilitersPerUnit))
                {
                    continue;
                }

                var density = gramWeight / (amount * millilitersPerUnit);
                suggestions.Add(new UsdaDensitySuggestion(
                    fdcId,
                    dataType,
                    description,
                    portionId,
                    amount,
                    portionDescription,
                    measureUnit,
                    gramWeight,
                    density));
            }
        }

        var result = suggestions
            .OrderBy(item => item.PortionDescription, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        cache.Set(cacheKey, result, TimeSpan.FromHours(12));
        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string stage,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var statusCode = response.StatusCode == HttpStatusCode.TooManyRequests
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;
            response.Dispose();
            throw new UsdaFoodDataException(statusCode, stage, "USDA FoodData Central request failed.");
        }
        catch (UsdaFoodDataException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsdaFoodDataException(StatusCodes.Status503ServiceUnavailable, stage, "USDA request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new UsdaFoodDataException(StatusCodes.Status503ServiceUnavailable, stage, "USDA is unavailable right now.");
        }
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new UsdaFoodDataException(
                StatusCodes.Status503ServiceUnavailable,
                "configuration",
                "USDA FoodData Central credentials are not configured.");
        }
    }

    private static string GetMeasureUnit(JsonElement portion)
    {
        if (portion.TryGetProperty("measureUnit", out var unit) && unit.ValueKind == JsonValueKind.Object)
        {
            return GetString(unit, "abbreviation") ?? GetString(unit, "name") ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool TryGetMillilitersPerUnit(
        string measureUnit,
        string portionDescription,
        out decimal milliliters)
    {
        if (TryMatchMeasure(measureUnit, out milliliters))
        {
            return true;
        }

        var match = Regex.Match(
            portionDescription,
            @"\b(?<unit>fluid\s+ounces?|fl\s*oz|millilit(?:er|re)s?|ml|lit(?:er|re)s?|l|teaspoons?|tsp|tablespoons?|tbsp|tbs|cups?|pints?|pt|quarts?|qt|gallons?|gal)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && TryMatchMeasure(match.Groups["unit"].Value, out milliliters);
    }

    private static bool TryMatchMeasure(string value, out decimal milliliters)
    {
        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        foreach (var measure in VolumeMeasures)
        {
            var measureName = Regex.Replace(measure.Name, @"[^\p{L}\p{N}]+", " ").Trim();
            if (normalized == measureName || normalized.StartsWith(measureName + " ", StringComparison.Ordinal))
            {
                milliliters = measure.Milliliters;
                return true;
            }
        }

        milliliters = 0;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static decimal GetDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            ? value
            : 0;
    }
}
