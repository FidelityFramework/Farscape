/* By-value parameters and results whose types reach typedefs carrying attributes (#181), each
   of which clang measures at the size and alignment of the type the descriptor claims: aligned(4)
   on int, int's own alignment, directly and through a second typedef; may_alias, which moves no
   byte; and aligned(4) on a record of int alignment. A pointer to an aligned(16) typedef reaches
   nothing: a pointer's layout is a pointer's. */
typedef int fx_bv_four __attribute__((aligned(4)));
typedef fx_bv_four fx_bv_four_chain;
typedef int fx_bv_alias __attribute__((may_alias));
typedef int fx_bv_wide __attribute__((aligned(16)));

struct fx_bv_pair
{
  int a;
  int b;
};

typedef struct fx_bv_pair fx_bv_pair4 __attribute__((aligned(4)));

fx_bv_four fx_bv_four_use(fx_bv_four_chain v, fx_bv_alias w);
void fx_bv_pair_use(fx_bv_pair4 p);
void fx_bv_pointer_use(fx_bv_wide *out);
