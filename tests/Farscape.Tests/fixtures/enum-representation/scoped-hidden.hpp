/* Issue #53 fixture: a scoped enum the file-scope probe cannot name; its use must be refused. */
namespace fx_ns {
enum class fx_hidden : unsigned char { FX_HIDDEN_A = 1 };
extern "C" int fx_take_hidden(fx_hidden value);
}
