/* C11 anonymous members: unnamed structs and unions with no field name, whose members belong
   to the enclosing record (fx_am.i, fx_am.lo), nested one inside another in fx_am_td. */
struct fx_am
{
  char tag;
  union { int i; float f; };
  struct { short lo; short hi; };
  long tail;
};

typedef struct
{
  int head;
  union
  {
    int word;
    struct { short low; short high; };
  };
} fx_am_td;

void fx_am_read(struct fx_am *am, fx_am_td *td);
