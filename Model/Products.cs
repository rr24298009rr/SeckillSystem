using System.ComponentModel.DataAnnotations;

namespace SeckillSystem.Model;

public class Products
{
    [Key]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    
    public decimal Price { get; set; }
    
    public int Stock { get; set; }
}
