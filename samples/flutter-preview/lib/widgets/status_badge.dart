import 'package:flutter/material.dart';

/// A badge that toggles between online and offline when tapped. It has no @Preview:
/// Vistudio previews it through a temporary wrapper project, or adds one with the quick fix.
class StatusBadge extends StatefulWidget {
  const StatusBadge({super.key});

  @override
  State<StatusBadge> createState() => _StatusBadgeState();
}

class _StatusBadgeState extends State<StatusBadge> {
  bool online = true;

  @override
  Widget build(BuildContext context) {
    return ActionChip(
      avatar: Icon(online ? Icons.circle : Icons.circle_outlined, color: online ? Colors.green : Colors.grey),
      label: Text(online ? 'Online' : 'Offline'),
      onPressed: () => setState(() => online = !online),
    );
  }
}
