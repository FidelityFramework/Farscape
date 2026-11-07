/* Issue #56 fixture: FX_SUM spelled with whitespace between tokens. */
#define FX_SUM 1 + 2
#define FX_WCHAR ( L'x' , u8"y" )
int fx_spaced(int value);
