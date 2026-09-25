using System.ComponentModel.DataAnnotations;
using InternTrackAI.Data;
using InternTrackAI.Services;
using InternTrackAI.Services.Gmail;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Areas.Identity.Pages.Account.Manage;

/// <summary>
/// Permanently deletes the signed-in user's account after a password confirmation. All app data
/// (applications, notes, letters, uploads, profile) is purged via <see cref="UserDataPurger"/>
/// before the Identity user row is removed, so nothing is orphaned. A connected Gmail grant is revoked
/// with Google first (best effort, as Disconnect does): deleting only the local row would leave the app
/// listed under the user's Google account with read access to an inbox whose owner has left.
/// </summary>
public class DeletePersonalDataModel : PageModel
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly UserDataPurger _purger;
    private readonly ApplicationDbContext _db;
    private readonly GmailGrantRevoker _revoker;
    private readonly ILogger<DeletePersonalDataModel> _logger;
    private readonly IConfiguration _config;

    public DeletePersonalDataModel(
        UserManager<IdentityUser> userManager,
        SignInManager<IdentityUser> signInManager,
        UserDataPurger purger,
        ApplicationDbContext db,
        GmailGrantRevoker revoker,
        ILogger<DeletePersonalDataModel> logger,
        IConfiguration config)
    {
        _config = config;
        _userManager = userManager;
        _signInManager = signInManager;
        _purger = purger;
        _db = db;
        _revoker = revoker;
        _logger = logger;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public bool RequirePassword { get; set; }

    /// <summary>Read-only for the shared demo account; POSTs never reach the handler (DemoAccountGuardFilter).</summary>
    public bool IsDemoAccount => ConfiguredAccounts.IsDemoUser(User, _config);

    public class InputModel
    {
        [Required(ErrorMessage = "Enter your password to confirm.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        RequirePassword = await _userManager.HasPasswordAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        RequirePassword = await _userManager.HasPasswordAsync(user);
        if (RequirePassword)
        {
            if (!ModelState.IsValid) return Page();
            if (!await _userManager.CheckPasswordAsync(user, Input.Password))
            {
                ModelState.AddModelError(string.Empty, "Incorrect password.");
                return Page();
            }
        }

        var userId = user.Id;

        // Not the request's token: a closed tab must not stop a deletion halfway. The revoker caps itself.
        var gmail = await _db.GmailConnections.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId);
        if (gmail is not null) await _revoker.RevokeAsync(gmail);

        await _purger.PurgeAsync(userId);

        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
        {
            _logger.LogError("Unexpected error deleting user {UserId}: {Errors}", userId,
                string.Join("; ", result.Errors.Select(e => e.Description)));
            ModelState.AddModelError(string.Empty, "Something went wrong deleting your account. Please try again.");
            return Page();
        }

        await _signInManager.SignOutAsync();
        _logger.LogInformation("User {UserId} deleted their account.", userId);

        TempData["Toast"] = "success|Your account and all its data have been deleted.";
        return Redirect("~/");
    }
}
