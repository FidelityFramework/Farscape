/* Records whose field types reach a typedef carrying aligned(8) (#175), Codex's counterexample
   (#174 comment 5521) without may_alias. Clang lays out fx_wide at 16 bytes, alignment 8, with
   `value` at 8; natural alignment of the declared field type `int` would give 8 bytes,
   alignment 4, `value` at 4. fx_chained reaches the attribute through a second typedef.
   fx_pointed holds a pointer to the aligned type, whose own layout the attribute cannot move.
   fx_ignored's typedef carries packed, which clang ignores on a typedef (-Wignored-attributes)
   and attaches to nothing, so the record is laid out at 8 bytes, alignment 4, `value` at 4. */
typedef int fx_wide_int __attribute__((aligned(8)));
typedef fx_wide_int fx_wide_chain;
typedef int fx_packed_int __attribute__((packed));

struct fx_wide
{
  char tag;
  fx_wide_int value;
};

struct fx_chained
{
  char tag;
  fx_wide_chain value;
};

struct fx_pointed
{
  char tag;
  fx_wide_int *value;
};

struct fx_ignored
{
  char tag;
  fx_packed_int value;
};

void fx_wide_use(struct fx_wide *wide, struct fx_chained *chained, struct fx_pointed *pointed, struct fx_ignored *ignored);
