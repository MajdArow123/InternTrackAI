using System.ComponentModel.DataAnnotations;
using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Areas.Identity.Pages.Account.Manage;

public class IndexModel : PageModel
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly ApplicationDbContext _db;

    public IndexModel(
        UserManager<IdentityUser> userManager,
        SignInManager<IdentityUser> signInManager,
        ApplicationDbContext db)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
    }

    public string Email { get; set; } = string.Empty;
    public bool IsEmailConfirmed { get; set; }

    [TempData] public string? StatusMessage { get; set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [StringLength(50, ErrorMessage = "Display name must be under 50 characters.")]
        [Display(Name = "Display name")]
        public string? DisplayName { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        Email = user.Email ?? string.Empty;
        IsEmailConfirmed = user.EmailConfirmed;

        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
        Input.DisplayName = profile?.DisplayName;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        if (!ModelState.IsValid)
        {
            Email = user.Email ?? string.Empty;
            return Page();
        }

        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
        if (profile == null)
        {
            profile = new UserProfile { UserId = user.Id };
            _db.UserProfiles.Add(profile);
        }

        profile.DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim();
        await _db.SaveChangesAsync();

        StatusMessage = "Display name updated.";
        return RedirectToPage();
    }
}
