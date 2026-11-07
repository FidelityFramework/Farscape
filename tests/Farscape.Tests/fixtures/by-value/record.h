/* Records passed and returned by value, named directly and through a typedef. */
typedef struct
{
  float x;
  float y;
} fx_vec_t;

struct fx_point
{
  int x;
  int y;
};

typedef struct fx_point fx_point_t;

void fx_take_vec(fx_vec_t v);
fx_point_t fx_make_point(int x, int y);

/* A callback taking and returning a record by value. */
typedef fx_vec_t (*fx_vec_map)(fx_vec_t value);
void fx_map_vec(fx_vec_map map) __attribute__((nonnull(1)));
