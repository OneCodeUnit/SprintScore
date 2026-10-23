using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SprintScore.Pages;

/// <summary>
/// Страница ведущего. Аутентификации нет по требованию: ведущим становится любой,
/// кто открыл /admin.
/// </summary>
public class AdminModel : PageModel
{
}
