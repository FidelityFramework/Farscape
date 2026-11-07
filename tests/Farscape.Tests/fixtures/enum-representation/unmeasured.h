/* Issue #53 fixture: a forward-declared enum clang cannot complete, used by value. */
enum fx_opaque;
int fx_take(enum fx_opaque value);
