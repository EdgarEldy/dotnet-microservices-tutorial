namespace Catalog.API.Models;

public class Product
{
    public int Id { get; set; }

    /// <summary>A real foreign key: categories and products both live in catalog-db.</summary>
    public int CategoryId { get; set; }

    public Category Category { get; set; } = null!;

    public required string ProductName { get; set; }

    public decimal UnitPrice { get; set; }
}
