using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.BotServices.Controllers.Account;

/// <summary>
///     This sample controller implements a typical login/logout/provision workflow for local and external accounts.
///     The login service encapsulates the interactions with the user data store. This data store is in-memory only and
///     cannot be used for production!
///     The interaction service provides a way for the UI to communicate with identityserver for validation and context
///     retrieval
/// </summary>
[AllowAnonymous]
public class AccountController : Controller
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public AccountController()
    {
    }

    /// <summary>
    ///     Handles the access denied page
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public IActionResult AccessDenied()
    {
        return View();
    }
}
