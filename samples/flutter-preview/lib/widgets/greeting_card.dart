import 'package:flutter/material.dart';
import 'package:flutter/widget_previews.dart';

/// A card with a greeting. Previewed by the two @Preview functions below.
class GreetingCard extends StatelessWidget {
  const GreetingCard({super.key, this.name = 'Vistudio'});

  final String name;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Text('Hello, $name!', style: Theme.of(context).textTheme.titleMedium),
      ),
    );
  }
}

@Preview(name: 'Greeting card', size: Size(320, 120))
Widget greetingCardPreview() => const GreetingCard();

@Preview(name: 'Dark greeting', brightness: Brightness.dark, textScaleFactor: 1.3)
Widget darkGreetingPreview() => const GreetingCard(name: 'Dark mode');
