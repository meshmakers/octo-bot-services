using System;
using System.Text;
using System.Threading.Tasks;
using Meshmakers.Octo.Backend.BotServices.Views.Home;
using Meshmakers.Octo.Backend.DistributedCache;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.BotServices.Controllers.Home;

public class HomeController : Controller
{
    private readonly IDistributedWithPubSubCache _distributedCache;

    public HomeController(IDistributedWithPubSubCache distributedCache)
    {
        _distributedCache = distributedCache;
    }

    // GET
    public async Task<IActionResult> Index()
    {
        var model = new IndexModel(_distributedCache);
        await model.OnGetAsync();
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> OnPostResetCachedTime()
    {
        var currentTimeUTC = DateTime.UtcNow.ToString();
        var encodedCurrentTimeUTC = Encoding.UTF8.GetBytes(currentTimeUTC);
        await _distributedCache.Database.StringSetAsync("cachedTimeUTC", encodedCurrentTimeUTC);

        return RedirectToAction("Index");
    }
}
