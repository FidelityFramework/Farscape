/* Issue #54 fixture: fx_word is 32 bits in this unit; fx_wide's field resolves through it. */
typedef unsigned int fx_word;
struct fx_wide { fx_word value; };
int fx_wide_read(struct fx_wide *record);
int fx_wide_take(fx_word value);
