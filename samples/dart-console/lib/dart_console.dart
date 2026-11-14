/// Shapes used by the console sample.
library;

abstract class Shape {
  double get area;
  String describe() => '$runtimeType with area ${area.toStringAsFixed(2)}';
}

class Rectangle extends Shape {
  Rectangle(this.width, this.height);

  final double width;
  final double height;

  @override
  double get area => width * height;
}

class Circle extends Shape {
  Circle(this.radius);

  final double radius;

  @override
  double get area => 3.141592653589793 * radius * radius;
}

/// Sum of the areas, rounded to two decimals.
double totalArea(List<Shape> shapes) {
  var total = 0.0;
  for (final shape in shapes) {
    total += shape.area;
  }
  return double.parse(total.toStringAsFixed(2));
}

int add(int a, int b) => a + b;
