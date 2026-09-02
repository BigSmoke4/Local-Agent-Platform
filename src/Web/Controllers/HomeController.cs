using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

public class HomeController : Controller
{
    [Route("/Home/Error")]
    public IActionResult Error() => View();
}
