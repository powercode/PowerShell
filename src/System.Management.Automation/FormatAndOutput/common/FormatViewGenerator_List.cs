// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Internal;

namespace Microsoft.PowerShell.Commands.Internal.Format
{
    internal sealed class ListViewGenerator : ViewGenerator
    {
        // tableBody to use for this instance of the ViewGenerator;
        private ListControlBody _listBody;

        internal override void Initialize(TerminatingErrorContext terminatingErrorContext, PSPropertyExpressionFactory mshExpressionFactory, TypeInfoDataBase db, ViewDefinition view, FormattingCommandLineParameters formatParameters)
        {
            base.Initialize(terminatingErrorContext, mshExpressionFactory, db, view, formatParameters);
            _listBody = (ListControlBody)dataBaseInfo?.view?.mainControl;
        }

        internal override void Initialize(TerminatingErrorContext errorContext, PSPropertyExpressionFactory expressionFactory,
                                    PSObject so, TypeInfoDataBase db, FormattingCommandLineParameters parameters)
        {
            base.Initialize(errorContext, expressionFactory, so, db, parameters);
            _listBody = (ListControlBody)dataBaseInfo?.view?.mainControl;

            inputParameters = parameters;
            SetUpActiveProperties(so);
        }

        /// <summary>
        /// Let the view prepare itself for RemoteObjects. This will add "ComputerName" to the
        /// table columns.
        /// </summary>
        /// <param name="so"></param>
        internal override void PrepareForRemoteObjects(PSObject so)
        {
            Diagnostics.Assert(so != null, "so cannot be null");

            // make sure computername property exists.
            Diagnostics.Assert(so.Properties[RemotingConstants.ComputerNameNoteProperty] != null,
                "PrepareForRemoteObjects cannot be called when the object does not contain ComputerName property.");

            var viewMainControl = dataBaseInfo?.view?.mainControl;
            if (viewMainControl != null)
            {
                _listBody = (ListControlBody)viewMainControl.Copy();
                // build up the definition for computer name.
                var cnListItemDefinition = new ListControlItemDefinition
                {
                    label = new TextToken
                    {
                        text = RemotingConstants.ComputerNameNoteProperty
                    }
                };
                var fieldPropertyToken = new FieldPropertyToken
                {
                    expression = new ExpressionToken(RemotingConstants.ComputerNameNoteProperty, false)
                };
                cnListItemDefinition.formatTokenList.Add(fieldPropertyToken);

                _listBody.defaultEntryDefinition.itemDefinitionList.Add(cnListItemDefinition);
            }
        }

        internal override FormatStartData GenerateStartData(PSObject so)
        {
            FormatStartData startFormat = base.GenerateStartData(so);
            startFormat.shapeInfo = new ListViewHeaderInfo();
            return startFormat;
        }

        internal override FormatEntryData GeneratePayload(PSObject so, int enumerationLimit)
        {
            return new FormatEntryData
            {
                formatEntryInfo = dataBaseInfo.view != null 
                    ? GenerateListViewEntryFromDataBaseInfo(so, enumerationLimit)
                    : GenerateListViewEntryFromProperties(so, enumerationLimit)
            };
        }

        private ListViewEntry GenerateListViewEntryFromDataBaseInfo(PSObject so, int enumerationLimit)
        {
            ListViewEntry lve = new ListViewEntry();

            var activeListControlEntryDefinition = GetActiveListControlEntryDefinition(_listBody, so);
            DisplayResourceManagerCache dbDisplayResourceManagerCache = dataBaseInfo.db.displayResourceManagerCache;
            var listControlItemDefinitions = activeListControlEntryDefinition.itemDefinitionList;

            for (var index = 0; index < listControlItemDefinitions.Count; index++)
            {
                ListControlItemDefinition listItem = listControlItemDefinitions[index];
                if (!EvaluateDisplayCondition(so, listItem.conditionToken))
                    continue;

                var propertyField = GenerateFormatPropertyField(listItem.formatTokenList, so, enumerationLimit, out PSPropertyExpressionResult result);
                string label = listItem.label != null
                    ? dbDisplayResourceManagerCache.GetTextTokenString(listItem.label) // if the directive provides one, we use it
                    : result != null
                        ? result.ResolvedExpression.ToString() // if we got a valid match from the Mshexpression, use it as a label
                        : listItem.formatTokenList[0] is FieldPropertyToken fpt
                            ? expressionFactory.CreateFromExpressionToken(fpt.expression, dataBaseInfo.view.loadingInfo).ToString()
                            : listItem.formatTokenList[0] is TextToken tt
                                ? dbDisplayResourceManagerCache.GetTextTokenString(tt)
                                : string.Empty;

                var lvf = new ListViewField
                {
                    formatPropertyField = propertyField,
                    label = label
                };

                lve.listViewFieldList.Add(lvf);
            }

            return lve;
        }

        private ListControlEntryDefinition GetActiveListControlEntryDefinition(ListControlBody listBody, PSObject so)
        {
            // see if we have an override that matches
            var typeNames = so.InternalTypeNames;
            TypeMatch match = new TypeMatch(expressionFactory, dataBaseInfo.db, typeNames);
            foreach (ListControlEntryDefinition x in listBody.optionalEntryList)
            {
                if (match.PerfectMatch(new TypeMatchItem(x, x.appliesTo, so)))
                {
                    return x;
                }
            }
            if (match.BestMatch != null)
            {
                return match.BestMatch as ListControlEntryDefinition;
            }

            Collection<string> typesWithoutPrefix = Deserializer.MaskDeserializationPrefix(typeNames);
            if (typesWithoutPrefix != null)
            {
                match = new TypeMatch(expressionFactory, dataBaseInfo.db, typesWithoutPrefix);
                foreach (ListControlEntryDefinition x in listBody.optionalEntryList)
                {
                    if (match.PerfectMatch(new TypeMatchItem(x, x.appliesTo)))
                    {
                        return x;
                    }
                }
                if (match.BestMatch != null)
                {
                    return match.BestMatch as ListControlEntryDefinition;
                }
            }

            // we do not have any override, use default
            return listBody.defaultEntryDefinition;
        }

        private ListViewEntry GenerateListViewEntryFromProperties(PSObject so, int enumerationLimit)
        {
            // compute active properties every time
            if (activeAssociationList == null)
            {
                SetUpActiveProperties(so);
            }

            var listViewEntry = new ListViewEntry();

            for (int k = 0; k < activeAssociationList.Count; k++)
            {
                MshResolvedExpressionParameterAssociation a = activeAssociationList[k];

                MshParameter aOriginatingParameter = a.OriginatingParameter;

                var key = aOriginatingParameter?.GetEntry(FormatParameterDefinitionKeys.LabelEntryKey);
                string lvfPropertyName = key == null
                    ? a.ResolvedExpression.ToString()
                    : key != AutomationNull.Value
                        ? (string)key
                        : a.ResolvedExpression.ToString();


                var listViewField = new ListViewField
                {
                    propertyName = lvfPropertyName
                };

                var formattingDirective = aOriginatingParameter?.GetEntry(FormatParameterDefinitionKeys.FormatStringEntryKey) as FieldFormattingDirective;
                
                listViewField.formatPropertyField.propertyValue = GetExpressionDisplayValue(so, enumerationLimit, a.ResolvedExpression, formattingDirective);
                listViewEntry.listViewFieldList.Add(listViewField);
            }

            activeAssociationList = null;
            return listViewEntry;
        }

        private void SetUpActiveProperties(PSObject so)
        {
            List<MshParameter> mshParameterList = null;

            if (inputParameters != null)
                mshParameterList = inputParameters.mshParameterList;

            activeAssociationList = AssociationManager.SetupActiveProperties(mshParameterList, so, expressionFactory);
        }
    }
}

