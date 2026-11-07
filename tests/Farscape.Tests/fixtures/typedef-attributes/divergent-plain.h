/* fx_div as divergent-aligned.h declares it, with a field typedef carrying no attribute. */
typedef int fx_div_int;

struct fx_div
{
  char tag;
  fx_div_int value;
};

void fx_div_plain(struct fx_div *div);
