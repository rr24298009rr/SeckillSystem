using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using SeckillSystem.Infrastructure;

namespace SeckillSystem.Controllers;

[ApiController]
[Route("[controller]")]
public class SeckillController : ControllerBase
{
    private readonly IDatabase _redis;
    private readonly MsDbContext _dbContext;

    public SeckillController(IConnectionMultiplexer redis, MsDbContext dbContext)
    {
        _redis = redis.GetDatabase();
        _dbContext = dbContext;
    }

    [HttpPost]
    public async Task<IActionResult> Purchase()
    {
        string key = "product_stock";
        const int productId = 1;
        
        try
        {
            // 先嘗試從 Redis 扣庫存（原子操作）
            long newStock = await _redis.StringDecrementAsync(key);
            
            // 如果扣減後庫存為負數，表示超賣或庫存不足
            if (newStock < 0)
            {
                // 回滾 Redis 操作
                await _redis.StringIncrementAsync(key);
                
                // 嘗試從資料庫重新載入庫存到 Redis
                var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.Id == productId);
                if (product != null && product.Stock > 0)
                {
                    await _redis.StringSetAsync(key, product.Stock);
                    // 重新嘗試扣減
                    newStock = await _redis.StringDecrementAsync(key);
                    if (newStock < 0)
                    {
                        await _redis.StringIncrementAsync(key);
                        return BadRequest(new { message = "搶購完畢" });
                    }
                }
                else
                {
                    // 資料庫也沒庫存，設置 Redis 為 0
                    await _redis.StringSetAsync(key, 0);
                    return BadRequest(new { message = "搶購完畢" });
                }
            }
            
            // Redis 扣減成功，開始資料庫事務
            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            
            try
            {
                // 更新資料庫庫存（樂觀鎖定）
                var product = await _dbContext.Products
                    .FirstOrDefaultAsync(p => p.Id == productId);
                
                if (product == null)
                {
                    throw new InvalidOperationException("商品不存在");
                }
                
                if (product.Stock <= 0)
                {
                    throw new InvalidOperationException("資料庫庫存不足");
                }
                
                // 扣減資料庫庫存
                product.Stock--;
                //product.UpdatedAt = DateTime.Now;
                
                // TODO: 這裡應該加入訂單記錄
                // var order = new Order 
                // {
                //     ProductId = productId,
                //     UserId = userId, // 需要從認證中獲取
                //     Quantity = 1,
                //     Price = product.Price,
                //     CreatedAt = DateTime.Now
                // };
                // _dbContext.Orders.Add(order);
                
                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                
                return Ok(new { 
                    message = "搶購成功", 
                    left = newStock,
                    dbStock = product.Stock,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception dbEx)
            {
                // 資料庫操作失敗，回滾事務和 Redis
                await transaction.RollbackAsync();
                await _redis.StringIncrementAsync(key);
                
                return BadRequest(new { 
                    message = "搶購失敗，請重試", 
                    error = dbEx.Message 
                });
            }
        }
        catch (Exception ex)
        {
            // Redis 操作失敗
            return BadRequest(new { 
                message = "系統錯誤，請稍後再試", 
                error = ex.Message 
            });
        }
    }
}
