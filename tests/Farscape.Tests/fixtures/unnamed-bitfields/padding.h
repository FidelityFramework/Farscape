/* Records carrying unnamed bit-fields (#176), each laid out by clang:
   fx_pad:        `int : 3` pads after `a`; `b` at 2, 3 bytes, alignment 1.
   fx_zero:       the zero-width `int : 0` moves `b` to the next int boundary; `b` at 4, 5 bytes.
   fx_zero_wide:  `long long : 0` moves `b` to the next 8-byte boundary; `b` at 8, 9 bytes.
   fx_nested:     an unnamed record field whose zero-width bit-field moves `c` to 4 within it;
                  the record is 5 bytes at 1 in fx_nested, which is 6 bytes.
   fx_member_pad: a C11 anonymous member (#173) before the zero-width bit-field, so the
                  bit-field is clang's third member; `d` at 4, 6 bytes, alignment 2.
   Natural alignment of the named fields alone would place every `b` and `c` at 1. */
struct fx_pad
{
  char a;
  int : 3;
  char b;
};

struct fx_zero
{
  char a;
  int : 0;
  char b;
};

struct fx_zero_wide
{
  char a;
  long long : 0;
  char b;
};

struct fx_nested
{
  char a;
  struct
  {
    char b;
    int : 0;
    char c;
  } inner;
};

struct fx_member_pad
{
  char a;
  union
  {
    char b;
    short c;
  };
  int : 0;
  char d;
};

void fx_pad_use(struct fx_pad *pad, struct fx_zero *zero, struct fx_zero_wide *wide, struct fx_nested *nested,
                struct fx_member_pad *member);
