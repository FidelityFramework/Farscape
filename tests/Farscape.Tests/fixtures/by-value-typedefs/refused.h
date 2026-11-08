/* By-value parameters whose types reach aligned(16) typedefs (#181): clang measures each at
   alignment 16, while the type its descriptor claims (int, struct fx_bv_wide_pair) has its own
   alignment of 4. A descriptor has no slot for a typedef's attribute, so each is refused. */
typedef int fx_bv_wide_int __attribute__((aligned(16)));

struct fx_bv_wide_pair
{
  int a;
  int b;
};

typedef struct fx_bv_wide_pair fx_bv_wide_pair16 __attribute__((aligned(16)));

void fx_bv_wide_int_use(fx_bv_wide_int v);
void fx_bv_wide_pair_use(fx_bv_wide_pair16 p);
