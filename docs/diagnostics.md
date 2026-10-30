# Diagnostics

IxIFlow 1.0.0 ships the following compiler diagnostics. Unless indicated otherwise, diagnostics are errors. The diagnostic location identifies the mapping, declaration or YAML source to correct.

## IXIF0001

**Input mapped into a property that is not an input.** Map activity inputs only to properties admitted by the activity input contract.

## IXIF0002

**Output mapped from a property that is not an output.** Read activity outputs only from properties admitted by the activity output contract.

## IXIF0003

**Required input is not mapped.** Map each required input or supply a valid default value.

## IXIF0004

**Mapping names no public property.** Mapping targets must be public instance properties.

## IXIF0005

**Activity input needs a public setter.** Give activity input properties public setters.

## IXIF0006

**Activity output needs a public getter.** Give activity output properties public getters.

## IXIF0007

**Required is invalid on an output.** Apply Required to inputs, not output-only properties.

## IXIF0008

**DefaultValue needs a public setter.** A property with DefaultValue must have a public setter.

## IXIF0009

**DefaultValue cannot be converted.** Use a DefaultValue that can be converted to the property type.

## IXIF0010

**Child workflow mapping requires an input.** Map child workflow inputs to WorkflowInput or WorkflowInputOutput properties.

## IXIF0011

**Child workflow mapping requires an output.** Read child workflow outputs from WorkflowOutput or WorkflowInputOutput properties.

## IXIF0012

**Required child workflow input is not mapped.** Map every required child workflow input that has no default.

## IXIF0013

**Workflow input needs a public setter.** Give workflow input properties public setters.

## IXIF0014

**Workflow output needs a public getter.** Give workflow output properties public getters.

## IXIF0015

**Required is invalid on a workflow output.** Apply Required to workflow inputs, not output-only properties.

## IXIF0016

**Workflow default value needs a public setter.** A workflow property with DefaultValue needs a public setter.

## IXIF0017

**Workflow default value cannot be converted.** Use a workflow DefaultValue that converts to the property type.

## IXIF0018

**Scope value escapes saved configuration.** Keep live scope resources out of saved public configuration properties. This rule is a warning.

## IXIF0019

**Scope requirement needs an activity.** Declare scope requirements on activity types.

## IXIF0020

**Scope cannot execute as an ordinary activity.** Implement either an ordinary activity or a scope activity on a type; their execution lifecycles differ.

## IXIF0021

**Scope lifecycle cannot use delegate inputs.** Use saved configuration values for scope inputs; scope lifecycle code has no workflow data for delegates.

## IXIF0022

**Invalid scope execution mode.** Use a supported scope threading and concurrency combination.

## IXIF0101

**Workflow expression is not pure.** Keep workflow expressions pure because recovery evaluates them again. Perform external effects in activities.

## IXIF0102

**Combiner is not pure.** Keep combiners pure because merging may run again after recovery.

## IXIYAML001

**Invalid workflow YAML.** Correct the workflow YAML or authored source at the reported location; parsing, validation or translation failed.

## IXIYAML002

**Activity root requires standalone authoring.** Compile activity documents through the standalone authoring API. Workflow additional files accept workflow roots.

