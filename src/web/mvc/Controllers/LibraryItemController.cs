using webapp.mvc.DataAccessLayer;

using webapp.mvc.Models;
using webapp.mvc.Models.ViewModels;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Rendering;
using webapp.mvc.Services;

namespace webapp.mvc.Controllers;
/*
    Internal research notes 
    HTTP Request comes in and goes to
                |-> Middleware
                |-> Routing
                |-> Controller initialization (this is where the factory comes to play)
                |-> Action Method Execution
                |                     -----------> Data result sent out
                |                   /
                |-> Result Execution
                                    \
                                     \-> View result
                                     |-> View Rendering -> Response sent out

*/
public class LibraryItemController : Controller {
    private readonly ILogger<LibraryItemController> _logger;
    private LibraryContext db;

    // The ordering of library items. Stays alive for the session or until a user idles out (after 1 hr)
    private String SessionOrdering {
        get {
            if (this.HttpContext.Session.GetString("Ordering") == null) {
                this.SessionOrdering = "cat";
            }
            return this.HttpContext.Session.GetString("Ordering")!;
        }
        set { this.HttpContext.Session.SetString("Ordering", value); }
    }

    public LibraryItemController(ILogger<LibraryItemController> logger, LibraryContext ctx) {
        _logger = logger;
        db = ctx;
    }

    [Route("/LibraryItem/Index")]
    public async Task<IActionResult> Index(string sortBy, string searchString, int? page, [FromServices] PageSizeService pageSizeService) {
        SessionOrdering = String.IsNullOrEmpty(sortBy) ? SessionOrdering : sortBy;
        ViewBag.CurrentPage = page ?? 1;
        ViewBag.Ordering = SessionOrdering;
        var items = from i in db.libraryItems select i;

        if (searchString != null) {
            ViewBag.CurrentPage = 1;
            ViewBag.Filter = searchString;
        } else {
            searchString = ViewBag.Filter;
        }

        items = String.IsNullOrEmpty(searchString) ? items.Include(e => e.Category) : items.Where(item => item.Title.Contains(searchString)).Include(e => e.Category);
        items = SessionOrdering switch {
            "cat" => items.OrderBy(i => i.Category.CategoryName),
            "cat_desc" => items.OrderByDescending(i => i.Category.CategoryName),
            "title" => items.OrderBy(i => i.Title),
            "title_desc" => items.OrderByDescending(i => i.Title),
            "type" => items.OrderBy(i => i.Type),
            "type_desc" => items.OrderByDescending(i => i.Type),
            _ => items
        };
        var viewModel = await items.GetPagedAsync(page ?? 1, pageSizeService.PageSize);
        ViewBag.CurrentPage = viewModel.PageIndex;
        _logger.LogDebug("Items:");
        if (viewModel == null) {
            _logger.LogError("COULD NOT RETRIEVE DATA!");
        } else {
            foreach (var i in viewModel.Page) {
                _logger.LogDebug($"[{i.ID}] [{i.Title}] [{i.Author}] [{i.Pages ?? i.RunTimeMinutes}]");
            }
        }
        return View(viewModel);
    }


    [HttpPost, ActionName("CheckOut")]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> CheckOut(int ID, string Borrower, DateTime? BorrowDate) {
        ModelState.Clear();
        if (await db.libraryItems.FindAsync(ID) is LibraryItem libraryItem) {
            if (libraryItem.Type == "reference book") {
                ModelState.AddModelError("Type", "You can't borrow a reference book");
            }
            if (String.IsNullOrWhiteSpace(Borrower)) {
                ModelState.AddModelError("Borrower", "You need to input name of borrower");
            }
            if (!BorrowDate.HasValue) {
                ModelState.AddModelError("BorrowDate", "You need to input name of borrower");
            }

            if (ModelState.IsValid) {
                libraryItem.Borrower = Borrower;
                libraryItem.BorrowDate = BorrowDate;
                db.Entry(libraryItem).State = EntityState.Modified;
                await db.SaveChangesAsync();
                return RedirectToAction("Index");
            }
            var editModel = await db.EditLibraryModel(ID);
            editModel!.ViewTab = "CheckOutTab";
            return View("Edit", editModel);
        } else {
            return new NotFoundResult();
        }

    }

    // Controller action that "returns" a library item
    [HttpGet, ActionName("CheckIn")]
    public async Task<ActionResult> CheckIn(int? id) {
        if (await db.libraryItems.FindAsync(id) is LibraryItem libraryItem) {
            libraryItem.BorrowDate = null;
            libraryItem.Borrower = "";
            if (ModelState.IsValid) {
                db.Entry(libraryItem).State = EntityState.Modified;
                await db.SaveChangesAsync();
            }
            return RedirectToAction("Index");
        } else {
            return new NotFoundResult();
        }
    }

    // GET method
    [HttpGet]
    public async Task<ActionResult> Create() {
        // We return an empty view, because, we let our custom UI library handle the populating of fields (like the Categories drop down list).
        // Note to consid: Since I am new to C#, ASP and it's frameworks, I realize that praxis is that one uses the Model-View-ViewModel design
        // but, at some point, I have to hand in this assignment. With the understanding of asp core that I have now, after a week and a half, I would have
        // instead went with that designs. Instead, I handle a lot of this stuff with Javascript calling into controller actions from the client side
        var createLibItemViewModel = new Models.ViewModels.CreateLibraryItemModel();
        createLibItemViewModel.Categories = await db.categoryItems.Select(c => new SelectListItem { Value = c.ID.ToString(), Text = c.CategoryName }).ToListAsync();
        if (createLibItemViewModel.Categories.Count() == 0) {
            ModelState.AddModelError("Category", "There exists no categories. You must first create a category where you can add this item to.");
        }
        return View(createLibItemViewModel);
    }

    // POST METHOD
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> Create([Bind("Title, Author, Length, Type, CategoryID")] Models.ViewModels.CreateLibraryItemModel item) {
        ModelState.Remove("Categories");
        var libItem = item.ToLibraryItem();
        if (libItem == null) {
            ModelState.AddModelError("Type", "Unknown type was set");
        }

        if (ModelState.IsValid && libItem != null) {
            db.libraryItems.Add(libItem);
            await db.SaveChangesAsync();
            return RedirectToAction("Index");
        } else {
            item.Categories = await db.categoryItems.Select(c => new SelectListItem { Value = c.ID.ToString(), Text = c.CategoryName }).ToListAsync();
            if (item.Categories.Count() == 0) {
                ModelState.AddModelError("Category", "There exists no categories. You must first create a category where you can add this item to.");
            }
        }
        return View(item);
    }

    // POST METHOD
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> Create2([Bind("CategoryID, Category, Title,Author,Pages,RunTimeMinutes,IsBorrowable,Borrower,BorrowDate,Type")] LibraryItem libraryItem) {
        if (!libraryItem.BorrowDate.HasValue && libraryItem.Borrower == null) libraryItem.Borrower = ""; // as per requirement doc. for some reason, Borrower should not be nullable
        if (libraryItem.Type == "reference book" && libraryItem.BorrowDate.HasValue) {
            ModelState.AddModelError("Type", "Reference book can not be borrowed. Only books, dvd's and audio books can be borrowerd");
            @ViewBag.ErrorMessage = ""; //  "Reference book can not be borrwed";
                                        // return View(libraryItem);
        }
        if (!String.IsNullOrWhiteSpace(libraryItem.Borrower) && !libraryItem.BorrowDate.HasValue) {
            @ViewBag.ErrorMessage = ""; // "Borrow date was not set";
            ModelState.AddModelError("BorrowDate", "Borrow date was not set");
            // return View(libraryItem);
        }
        if (libraryItem.BorrowDate.HasValue && String.IsNullOrWhiteSpace(libraryItem.Borrower)) {
            @ViewBag.ErrorMessage = ""; // "Borrower did not have a name.";
            ModelState.AddModelError("Borrower", "Borrower lacked a name input");
            // return View(libraryItem);
        }
        var cat = await db.categoryItems.FindAsync(libraryItem.CategoryID);
        if (cat == null) return View(libraryItem);
        libraryItem.Category = cat;
        if (ModelState.IsValid) {
            db.libraryItems.Add(libraryItem);
            await db.SaveChangesAsync();
            return RedirectToAction("Index");
        }

        return View(libraryItem);
    }

    public async Task<ActionResult> Edit(int? id) {
        if (id == null) {
            return new BadRequestResult();
        }
        var editModel = await db.EditLibraryModel(id.Value);
        if (editModel == null) return new NotFoundResult();
        editModel.ViewTab = "EditDetailsTab";
        return View(editModel);
    }

    // POST METHOD
    [HttpPost, ActionName("Edit")]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> EditConfirmed([Bind("ID, Title, Author, Length, Type, CategoryID")] EditLibraryItemModel model) {
        ModelState.Remove("Categories");
        var dbItem = await db.libraryItems.FindAsync(model.ID);
        if (dbItem == null) {
            ModelState.AddModelError("ID", $"No library item with that ID exists: {model.ID}");
            return View(model);
        }
        var cat = await db.categoryItems.FindAsync(model.CategoryID);
        if (cat == null) {
            ModelState.AddModelError("CategoryID", $"No category with id {model.CategoryID} exists");
        }

        if (model.Type == "reference book" && dbItem.BorrowDate.HasValue) {
            ModelState.AddModelError("Type", "This book must be checked in first before it can be changed to a reference book");
        }

        TryValidateModel(model);
        ModelState.Remove("Categories");
        dbItem.Title = model.Title;
        dbItem.Author = model.Author;
        dbItem.Type = model.Type;
        dbItem.CategoryID = model.CategoryID;
        switch (model.Type) {
            case "reference book":
                dbItem.Pages = model.Length;
                dbItem.RunTimeMinutes = null;
                dbItem.IsBorrowable = false;
                break;
            case "book":
                dbItem.Pages = model.Length;
                dbItem.RunTimeMinutes = null;
                dbItem.IsBorrowable = true;
                break;
            case "audio book":
            case "dvd":
                dbItem.Pages = null;
                dbItem.RunTimeMinutes = model.Length;
                dbItem.IsBorrowable = true;
                break;
            default:
                ModelState.AddModelError("Type", $"Type {model.Type} does not exist");
                break;
        }
        if (ModelState.IsValid) {
            db.Entry(dbItem).State = EntityState.Modified;
            await db.SaveChangesAsync();
            return RedirectToAction("Index");
        } else {
            var categoriesList = await (from c in db.categoryItems select c).Select(c => new SelectListItem { Value = c.ID.ToString(), Text = c.CategoryName }).ToListAsync();
            model.Categories = categoriesList;
            model.ViewTab = "EditDetailsTab";
            return View(model);
        }
    }

    // POST METHOD
    [HttpPost, ActionName("FooEdit")]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> FooEditConfirmed(int ID, int CategoryID, string Title, string Author, int? Pages, int? RunTimeMinutes, bool IsBorrowable, string Borrower, DateTime? BorrowDate, string Type) {
        // NOTE(simon): Bug in ModelState validation? I've set this to "AllowEmptyStrings", I've tried setting it to "minimum length = 1" as well to no avail. All that's left
        // is clearing it manually. Yay OOP.
        ModelState.ClearValidationState("Borrower");
        var libraryItem = new LibraryItem { ID = ID, CategoryID = CategoryID, Title = Title, Author = Author, Pages = Pages, RunTimeMinutes = RunTimeMinutes, IsBorrowable = IsBorrowable, Borrower = Borrower, BorrowDate = BorrowDate, Type = Type };
        var cat = await db.categoryItems.FindAsync(libraryItem.CategoryID);
        if (cat == null) {
            ModelState.AddModelError("CategoryID", $"No category with id {libraryItem.CategoryID} exists");
        } else {
            libraryItem.Category = cat;
        }
        if (libraryItem.Type == "reference book" && libraryItem.BorrowDate.HasValue) {
            var old = await db.libraryItems.FindAsync(libraryItem.ID);
            if (old == null) {
                return new NotFoundResult();
            }
            var categoriesList = await (from c in db.categoryItems select c).Select(c => new SelectListItem { Value = c.ID.ToString(), Text = c.CategoryName }).ToListAsync();
            ViewBag.CategoriesDropdownList = categoriesList;
            ModelState.AddModelError("Type", "This book must be checked in first before it can be changed to a reference book");
        }

        if (!libraryItem.BorrowDate.HasValue) {
            libraryItem.Borrower = "";
        }
        TryValidateModel(libraryItem);
        if (ModelState.IsValid) {
            db.Entry(libraryItem).State = EntityState.Modified;
            await db.SaveChangesAsync();
            return RedirectToAction("Index");
        }
        return View(await db.libraryItems.FindAsync(ID));
    }

    // GET METHOD
    public async Task<ActionResult> Delete(int? id) {
        if (id == null) {
            return new BadRequestResult();
        }
        LibraryItem? libraryItem = await db.libraryItems.FindAsync(id);
        if (libraryItem == null) {
            return new NotFoundResult();
        }
        return View(libraryItem);
    }

    // POST METHOD
    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> DeleteConfirmed(int id) {
        LibraryItem? libraryItem = await db.libraryItems.FindAsync(id);
        if (libraryItem != null)
            db.libraryItems.Remove(libraryItem);
        return await db.SaveChangesAsync().ContinueWith(t => RedirectToAction("Index"));
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            db.Dispose();
        }
        base.Dispose(disposing);
    }
}