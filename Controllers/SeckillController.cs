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
    [Route("purchase/{id}")]
    public async Task<IActionResult> Purchase(int id)
    {
        string key = $"product_{id}_stock";

        try
        {
            RedisValue redisValue = await _redis.StringGetAsync(key);

            if (!redisValue.HasValue)
            {
                var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.Id == id);
                if (product == null || product.Stock <= 0)
                {
                    await _redis.StringSetAsync(key, 0);
                    return BadRequest(new { message = "商品售罄" });
                }

                await _redis.StringSetAsync(key, product.Stock);
            }

            // 🧊 嘗試 Redis 原子扣庫存
            long newStock = await _redis.StringDecrementAsync(key);

            if (newStock < 0)
            {
                // ❗️庫存為負，立即補回（回滾），不重試扣第二次
                await _redis.StringIncrementAsync(key);

                return BadRequest(new { message = "搶購完畢" });
            }

            // ✅ Redis 庫存足夠，開始處理資料庫層
            using var transaction = await _dbContext.Database.BeginTransactionAsync();

            try
            {
                var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.Id == id);

                if (product == null)
                {
                    throw new InvalidOperationException("商品不存在");
                }

                if (product.Stock <= 0)
                {
                    throw new InvalidOperationException("資料庫庫存不足");
                }

                // 🧮 更新資料庫庫存
                product.Stock--;

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

                return Ok(new
                {
                    message = "搶購成功",
                    left = newStock,
                    dbStock = product.Stock,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception dbEx)
            {
                // ❌ 資料庫失敗，補回 Redis 庫存
                await transaction.RollbackAsync();
                await _redis.StringIncrementAsync(key);

                return BadRequest(new
                {
                    message = "搶購失敗，請重試",
                    error = dbEx.Message
                });
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = "系統錯誤",
                error = ex.Message
            });
        }
    }

    [HttpPost]
    [Route("reset/{id}")]
    public async Task<IActionResult> Reset(int id)
    {
        string key = $"product_{id}_stock";

        try
        {
            // 先嘗試從 Redis 清除庫存
            var result = await _redis.KeyDeleteAsync(key);
            // 嘗試恢復 MSSQL 庫存
            var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.Id == id);
            if (product == null)
            {
                return NotFound(new { message = "商品不存在" });
            }

            // 嘗試恢復 MSSQL 庫存
            product.Stock = int.TryParse(product.Name.Substring(5, product.Name.Length - 5), out var stock) ? stock : 0;
            await _dbContext.SaveChangesAsync();

            return Ok(new { message = "重置成功" });
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = "重置失敗",
                error = ex.Message
            });
        }
    }
}
