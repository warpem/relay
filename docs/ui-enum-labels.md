# Enum option labels

`UiEnum` dropdowns use the standard `System.ComponentModel.DataAnnotations.DisplayAttribute`
on enum members for human-readable labels:

```csharp
public enum ParticleSchedule
{
    [Display(Name = "Quadratic ramp-up")] Quadratic,
    [Display(Name = "Step to full set")] Step,
    [Display(Name = "Full set throughout")] Full
}
```

Members without an annotation keep their existing name. `Display.GetName()` also
supports resource-backed labels. Only dropdown text changes: option values,
serialized job settings, conditional field dependencies and CLI argument values
continue to use the original enum names. Do not rename enum members to change a label.
