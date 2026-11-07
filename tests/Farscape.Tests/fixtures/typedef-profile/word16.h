/* Issue #54 fixture: fx_word is 16 bits in this unit. */
typedef unsigned short fx_word;
struct fx_narrow { fx_word value; };
int fx_narrow_read(struct fx_narrow *record);
int fx_narrow_take(fx_word value);
