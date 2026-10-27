using System.Globalization;
using System.Text.Json.Serialization;

namespace RecipeCost.Pricing.Application;

public sealed record UnitDefinition(
    int UnitId,
    string UnitType,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal ConversionToBase);

public sealed record UnitConversion(
    string IngredientId,
    int FromUnitId,
    int ToUnitId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal ConversionFactor);

public sealed record PriceObservation(
    string PriceId,
    string ProductId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal Price,
    string CurrencyCode,
    DateTimeOffset? RecordedAt,
    string? Source);

public sealed record ProductCandidate(
    string ProductId,
    string ProductName,
    string IngredientId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal PackageQuantity,
    UnitDefinition PackageUnit,
    PriceObservation? Price);

public sealed record RecipeLineInput(
    string RecipeIngredientId,
    string IngredientId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal Quantity,
    UnitDefinition Unit,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal? DensityGramsPerMl,
    IReadOnlyList<ProductCandidate> Candidates,
    IReadOnlyList<UnitConversion> Conversions);

public sealed record IngredientCostResult(
    string RecipeIngredientId,
    string IngredientId,
    string? ProductId,
    string? PriceId,
    decimal? Cost,
    string? CurrencyCode,
    DateTimeOffset? PriceRecordedAt,
    string? Source,
    string? Issue);

public sealed record RecipeCostResult(
    decimal KnownSubtotal,
    decimal? TotalCost,
    decimal? CostPerServing,
    string? CurrencyCode,
    bool IsComplete,
    IReadOnlyList<IngredientCostResult> Lines);

public sealed class PricingCalculator
{
    public RecipeCostResult CalculateRecipeCost(IEnumerable<RecipeLineInput> inputLines, int servings)
    {
        ArgumentNullException.ThrowIfNull(inputLines);
        if (servings <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(servings), "Servings must be greater than zero.");
        }

        var requestLines = inputLines.ToArray();
        var lines = requestLines.Select(CalculateLine).ToArray();
        var currencies = requestLines
            .SelectMany(line => line.Candidates)
            .Where(candidate => candidate.Price is not null)
            .Select(candidate => candidate.Price!.CurrencyCode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (currencies.Length > 1)
        {
            throw new InvalidOperationException(
                "A recipe cannot compare or total prices in different currencies.");
        }

        var knownSubtotal = lines
            .Where(line => line.Cost.HasValue)
            .Sum(line => line.Cost!.Value);
        var isComplete = lines.All(line => line.Cost.HasValue);

        return new RecipeCostResult(
            knownSubtotal,
            isComplete ? knownSubtotal : null,
            isComplete ? knownSubtotal / servings : null,
            currencies.FirstOrDefault(),
            isComplete,
            lines);
    }

    private static IngredientCostResult CalculateLine(RecipeLineInput line)
    {
        ValidateLine(line);

        if (line.Candidates.Count == 0)
        {
            return Missing(line, "missing_product");
        }

        var pricedCandidates = new List<(ProductCandidate Product, decimal Cost)>();
        var hasPrice = false;
        foreach (var candidate in line.Candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ValidateCandidate(line, candidate);
            if (candidate.Price is null)
            {
                continue;
            }

            hasPrice = true;
            ValidatePrice(candidate, candidate.Price);

            if (!TryConvertQuantity(
                    line.IngredientId,
                    line.Quantity,
                    line.Unit,
                    candidate.PackageUnit,
                    line.DensityGramsPerMl,
                    line.Conversions,
                    out var quantityInPackageUnit))
            {
                continue;
            }

            var cost = quantityInPackageUnit / candidate.PackageQuantity * candidate.Price.Price;
            pricedCandidates.Add((candidate, cost));
        }

        var selected = pricedCandidates
            .OrderBy(option => option.Cost)
            .ThenByDescending(option => option.Product.Price!.RecordedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (selected.Product is null)
        {
            return Missing(line, hasPrice ? "missing_conversion" : "missing_price");
        }

        return new IngredientCostResult(
            line.RecipeIngredientId,
            line.IngredientId,
            selected.Product.ProductId,
            selected.Product.Price!.PriceId,
            decimal.Round(selected.Cost, 2, MidpointRounding.AwayFromZero),
            selected.Product.Price.CurrencyCode,
            selected.Product.Price.RecordedAt,
            selected.Product.Price.Source,
            null);
    }

    private static IngredientCostResult Missing(RecipeLineInput line, string issue) =>
        new(line.RecipeIngredientId, line.IngredientId, null, null, null, null, null, null, issue);

    private static void ValidateLine(RecipeLineInput line)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(line.RecipeIngredientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(line.IngredientId);
        ValidateQuantity(line.Quantity, nameof(line.Quantity), allowZero: false);
        ValidateUnit(line.Unit, nameof(line.Unit));
        ArgumentNullException.ThrowIfNull(line.Candidates);
        ArgumentNullException.ThrowIfNull(line.Conversions);
        if (line.DensityGramsPerMl is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(line.DensityGramsPerMl),
                "Density must be greater than zero when supplied.");
        }
    }

    private static void ValidateCandidate(RecipeLineInput line, ProductCandidate candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ProductId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.IngredientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ProductName);
        if (!string.Equals(line.IngredientId, candidate.IngredientId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The product does not belong to the recipe ingredient.");
        }
        ValidateQuantity(candidate.PackageQuantity, nameof(candidate.PackageQuantity), allowZero: false);
        ValidateUnit(candidate.PackageUnit, nameof(candidate.PackageUnit));
    }

    private static void ValidatePrice(ProductCandidate product, PriceObservation price)
    {
        if (!string.Equals(product.ProductId, price.ProductId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The price does not belong to the selected product.");
        }
        ValidateQuantity(price.Price, nameof(price.Price));
        if (string.IsNullOrWhiteSpace(price.CurrencyCode))
        {
            throw new ArgumentException("A price must include a currency code.");
        }
    }

    private static void ValidateUnit(UnitDefinition unit, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.UnitId <= 0 || unit.ConversionToBase <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Unit conversion values must be positive.");
        }
        if (unit.UnitType is not ("weight" or "volume" or "count"))
        {
            throw new ArgumentException("Unit type must be weight, volume, or count.", parameterName);
        }
    }

    private static bool TryConvertQuantity(
        string ingredientId,
        decimal quantity,
        UnitDefinition from,
        UnitDefinition to,
        decimal? densityGramsPerMl,
        IEnumerable<UnitConversion> conversions,
        out decimal converted)
    {
        if (from.UnitId == to.UnitId)
        {
            converted = quantity;
            return true;
        }

        if (string.Equals(from.UnitType, to.UnitType, StringComparison.Ordinal))
        {
            converted = quantity * from.ConversionToBase / to.ConversionToBase;
            return true;
        }

        if (TryConvertThroughIngredientEdges(ingredientId, quantity, from.UnitId, to.UnitId, conversions, out converted))
        {
            return true;
        }

        if (densityGramsPerMl is > 0 && from.UnitType == "volume" && to.UnitType == "weight")
        {
            converted = quantity * from.ConversionToBase * densityGramsPerMl.Value / to.ConversionToBase;
            return true;
        }

        if (densityGramsPerMl is > 0 && from.UnitType == "weight" && to.UnitType == "volume")
        {
            converted = quantity * from.ConversionToBase / densityGramsPerMl.Value / to.ConversionToBase;
            return true;
        }

        converted = 0;
        return false;
    }

    private static bool TryConvertThroughIngredientEdges(
        string ingredientId,
        decimal quantity,
        int fromUnitId,
        int toUnitId,
        IEnumerable<UnitConversion> conversions,
        out decimal converted)
    {
        var edges = conversions
            .Where(edge => string.Equals(edge.IngredientId, ingredientId, StringComparison.Ordinal))
            .ToArray();
        var factors = new Dictionary<int, decimal> { [fromUnitId] = 1m };
        var pending = new Queue<int>();
        pending.Enqueue(fromUnitId);

        while (pending.TryDequeue(out var currentUnitId))
        {
            foreach (var edge in edges)
            {
                ValidateQuantity(edge.ConversionFactor, nameof(conversions), allowZero: false);
                if (edge.FromUnitId == currentUnitId)
                {
                    Add(edge.ToUnitId, factors[currentUnitId] * edge.ConversionFactor);
                }
                if (edge.ToUnitId == currentUnitId)
                {
                    Add(edge.FromUnitId, factors[currentUnitId] / edge.ConversionFactor);
                }
            }

            if (factors.TryGetValue(toUnitId, out var factor))
            {
                converted = quantity * factor;
                return true;
            }
        }

        converted = 0;
        return false;

        void Add(int unitId, decimal factor)
        {
            if (factors.TryAdd(unitId, factor))
            {
                pending.Enqueue(unitId);
            }
        }
    }

    private static void ValidateQuantity(decimal value, string parameterName, bool allowZero = true)
    {
        if (value < 0 || (!allowZero && value == 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Quantity and price values must be nonnegative.");
        }
    }

    public static string FormatMoney(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
}
