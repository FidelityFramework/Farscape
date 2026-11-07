/* A record returned by value that nests an unnamed union (audit codex-audit-m2f, #162). */
struct fx_outer_v
{
  char lead;
  union
  {
    long raw;
    struct
    {
      short left;
      int right;
    } parts;
  } cell;
  char tail;
};

struct fx_outer_v fx_use(void);
