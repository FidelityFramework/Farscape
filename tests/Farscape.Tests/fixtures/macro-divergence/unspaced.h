/* Issue #56 fixture: FX_SUM as the same tokens without whitespace. */
#define FX_SUM 1+2
#define FX_WCHAR (L'x',u8"y")
int fx_unspaced(int value);
