// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Internal;

namespace Microsoft.PowerShell.Commands.Internal.Format
{
    /// <summary>
    /// base class for the various types of formatting shapes
    /// </summary>
    internal abstract class ViewGenerator
    {
        internal virtual void Initialize(TerminatingErrorContext terminatingErrorContext,
                                        PSPropertyExpressionFactory mshExpressionFactory,
                                        TypeInfoDataBase db,
                                        ViewDefinition view,
                                        FormattingCommandLineParameters formatParameters)
        {
            Diagnostics.Assert(mshExpressionFactory != null, "mshExpressionFactory cannot be null");
            Diagnostics.Assert(db != null, "db cannot be null");
            Diagnostics.Assert(view != null, "view cannot be null");

            errorContext = terminatingErrorContext;
            expressionFactory = mshExpressionFactory;
            parameters = formatParameters;

            dataBaseInfo.db = db;
            dataBaseInfo.view = view;
            dataBaseInfo.applicableTypes = DisplayDataQuery.GetAllApplicableTypes(db, view.appliesTo);

            InitializeHelper();
        }

        internal virtual void Initialize(TerminatingErrorContext terminatingErrorContext,
                                            PSPropertyExpressionFactory mshExpressionFactory,
                                            PSObject so,
                                            TypeInfoDataBase db,
                                            FormattingCommandLineParameters formatParameters)
        {
            errorContext = terminatingErrorContext;
            expressionFactory = mshExpressionFactory;
            parameters = formatParameters;
            dataBaseInfo.db = db;

            InitializeHelper();
        }

        /// <summary>
        /// Let the view prepare itself for RemoteObjects. Specific view generators can
        /// use this call to customize display for remote objects like showing/hiding
        /// computername property etc.
        /// </summary>
        /// <param name="so"></param>
        internal virtual void PrepareForRemoteObjects(PSObject so)
        {
        }

        private void InitializeHelper()
        {
            InitializeFormatErrorManager();
            InitializeGroupBy();
            InitializeAutoSize();
        }

        private void InitializeFormatErrorManager()
        {
            var formatErrorPolicy = new FormatErrorPolicy
            {
                ShowErrorsAsMessages = parameters?.showErrorsAsMessages ?? dataBaseInfo.db.defaultSettingsSection.formatErrorPolicy.ShowErrorsAsMessages,
                ShowErrorsInFormattedOutput = parameters?.showErrorsInFormattedOutput ?? dataBaseInfo.db.defaultSettingsSection.formatErrorPolicy.ShowErrorsInFormattedOutput
            };

            _errorManager = new FormatErrorManager(formatErrorPolicy);
        }

        private void InitializeGroupBy()
        {
            // first check if there is an override from the command line
            if (parameters?.groupByParameter != null)
            {
                // get the expression to use
                PSPropertyExpression groupingKeyExpression = parameters.groupByParameter.GetEntry(FormatParameterDefinitionKeys.ExpressionEntryKey) as PSPropertyExpression;

                // set the label
                string label = null;
                object labelKey = parameters.groupByParameter.GetEntry(FormatParameterDefinitionKeys.LabelEntryKey);
                if (labelKey != AutomationNull.Value)
                {
                    label = labelKey as string;
                }
                _groupingManager = new GroupingInfoManager();
                _groupingManager.Initialize(groupingKeyExpression, label);
                return;
            }

            // check if we have a view to initialize from
            if (dataBaseInfo.view != null)
            {
                GroupBy gb = dataBaseInfo.view.groupBy;
                if (gb?.startGroup?.expression == null)
                {
                    return;
                }

                PSPropertyExpression ex = expressionFactory.CreateFromExpressionToken(gb.startGroup.expression, dataBaseInfo.view.loadingInfo);

                _groupingManager = new GroupingInfoManager();
                _groupingManager.Initialize(ex, null);
            }
        }

        private void InitializeAutoSize()
        {
            // check the autosize flag first
            if (parameters?.autosize != null)
            {
                _autosize = parameters.autosize.Value;
                return;
            }
            // check if we have a view with autosize checked
            if (dataBaseInfo.view?.mainControl != null)
            {
                if (dataBaseInfo.view.mainControl is ControlBody controlBody && controlBody.autosize.HasValue)
                {
                    _autosize = controlBody.autosize.Value;
                }
            }
        }

        internal virtual FormatStartData GenerateStartData(PSObject so)
        {
            FormatStartData startFormat = new FormatStartData();

            if (_autosize)
            {
                startFormat.autosizeInfo = new AutosizeInfo();
            }
            return startFormat;
        }

        internal abstract FormatEntryData GeneratePayload(PSObject so, int enumerationLimit);

        internal GroupStartData GenerateGroupStartData(PSObject firstObjectInGroup, int enumerationLimit)
        {
            GroupStartData startGroup = new GroupStartData();
            if (_groupingManager == null)
                return startGroup;

            object currentGroupingValue = _groupingManager.CurrentGroupingKeyPropertyValue;

            if (currentGroupingValue == AutomationNull.Value)
                return startGroup;

            PSObject so = PSObjectHelper.AsPSObject(currentGroupingValue);

            // we need to determine how to display the group header
            ControlBase control = null;
            TextToken labelTextToken = null;
            if (dataBaseInfo.view?.groupBy?.startGroup != null)
            {
                // NOTE: from the database constraints, only one of the
                // two will be non null
                control = dataBaseInfo.view.groupBy.startGroup.control;
                labelTextToken = dataBaseInfo.view.groupBy.startGroup.labelTextToken;
            }

            startGroup.groupingEntry = new GroupingEntry();

            if (control == null)
            {
                // we do not have a control, we auto generate a
                // snippet of complex display using a label

                StringFormatError formatErrorObject = null;
                if (_errorManager.DisplayFormatErrorString)
                {
                    // we send a format error object down to the formatting calls
                    // only if we want to show the formatting error strings
                    formatErrorObject = new StringFormatError();
                }

                string currentGroupingValueDisplay = PSObjectHelper.SmartToString(so, expressionFactory, enumerationLimit, formatErrorObject);

                if (formatErrorObject?.exception != null)
                {
                    // if we did no have any errors in the expression evaluation
                    // we might have errors in the formatting, if present
                    _errorManager.LogStringFormatError(formatErrorObject);
                    if (_errorManager.DisplayFormatErrorString)
                    {
                        currentGroupingValueDisplay = _errorManager.FormatErrorString;
                    }
                }

                FormatEntry fe = new FormatEntry();
                startGroup.groupingEntry.formatValueList.Add(fe);

                FormatTextField ftf = new FormatTextField();

                // determine what the label should be. If we have a label from the
                // database, let's use it, else fall back to the string provided
                // by the grouping manager
                string label = labelTextToken != null 
                    ? dataBaseInfo.db.displayResourceManagerCache.GetTextTokenString(labelTextToken)
                    : _groupingManager.GroupingKeyDisplayName;

                ftf.text = StringUtil.Format(FormatAndOut_format_xxx.GroupStartDataIndentedAutoGeneratedLabel, label);

                fe.formatValueList.Add(ftf);

                var fpf = new FormatPropertyField
                {
                    propertyValue = currentGroupingValueDisplay
                };

                fe.formatValueList.Add(fpf);
            }
            else
            {
                // NOTE: we set a max depth to protect ourselves from infinite loops
                const int maxTreeDepth = 50;
                var controlGenerator =
                                new ComplexControlGenerator(dataBaseInfo.db,
                                        dataBaseInfo.view.loadingInfo,
                                        expressionFactory,
                                        dataBaseInfo.view.formatControlDefinitionHolder.controlDefinitionList,
                                        ErrorManager,
                                        enumerationLimit);

                controlGenerator.GenerateFormatEntries(maxTreeDepth,
                    control, firstObjectInGroup, startGroup.groupingEntry.formatValueList);
            }
            return startGroup;
        }

        /// <summary>
        /// update the current value of the grouping key
        /// </summary>
        /// <param name="so">object to use for the update</param>
        /// <returns>true if the value of the key changed</returns>
        internal bool UpdateGroupingKeyValue(PSObject so)
        {
            if (_groupingManager == null)
                return false;
            return _groupingManager.UpdateGroupingKeyValue(so);
        }

        internal GroupEndData GenerateGroupEndData()
        {
            return new GroupEndData();
        }

        internal bool IsObjectApplicable(Collection<string> typeNames)
        {
            if (dataBaseInfo.view == null)
                return true;

            if (typeNames.Count == 0)
                return false;

            TypeMatch match = new TypeMatch(expressionFactory, dataBaseInfo.db, typeNames);
            if (match.PerfectMatch(new TypeMatchItem(this, dataBaseInfo.applicableTypes)))
            {
                return true;
            }

            bool result = match.BestMatch != null;

            // we were unable to find a best match so far..try
            // to get rid of Deserialization prefix and see if a
            // match can be found.
            if (false == result)
            {
                Collection<string> typesWithoutPrefix = Deserializer.MaskDeserializationPrefix(typeNames);
                if (typesWithoutPrefix != null)
                {
                    result = IsObjectApplicable(typesWithoutPrefix);
                }
            }
            return result;
        }

        private GroupingInfoManager _groupingManager = null;

        protected bool AutoSize
        {
            get { return _autosize; }
        }
        private bool _autosize = false;

        protected class DataBaseInfo
        {
            internal TypeInfoDataBase db = null;
            internal ViewDefinition view = null;
            internal AppliesTo applicableTypes = null;
        }

        protected TerminatingErrorContext errorContext;

        protected FormattingCommandLineParameters parameters;

        protected PSPropertyExpressionFactory expressionFactory;

        protected DataBaseInfo dataBaseInfo = new DataBaseInfo();

        protected List<MshResolvedExpressionParameterAssociation> activeAssociationList = null;
        protected FormattingCommandLineParameters inputParameters = null;

        protected string GetExpressionDisplayValue(PSObject so, int enumerationLimit, PSPropertyExpression ex,
                    FieldFormattingDirective directive)
        {
            return GetExpressionDisplayValue(so, enumerationLimit, ex, directive, out PSPropertyExpressionResult _);
        }

        protected string GetExpressionDisplayValue(PSObject so, int enumerationLimit, PSPropertyExpression ex,
                    FieldFormattingDirective directive, out PSPropertyExpressionResult expressionResult)
        {
            StringFormatError formatErrorObject = null;
            if (_errorManager.DisplayFormatErrorString)
            {
                // we send a format error object down to the formatting calls
                // only if we want to show the formatting error strings
                formatErrorObject = new StringFormatError();
            }

            string retVal = PSObjectHelper.GetExpressionDisplayValue(so, enumerationLimit, ex,
                                directive, formatErrorObject, expressionFactory, out expressionResult);

            if (expressionResult != null)
            {
                // we obtained a result, check if there is an error
                if (expressionResult.Exception != null)
                {
                    _errorManager.LogPSPropertyExpressionFailedResult(expressionResult, so);
                    if (_errorManager.DisplayErrorStrings)
                    {
                        retVal = _errorManager.ErrorString;
                    }
                }
                else if (formatErrorObject?.exception != null)
                {
                    // if we did no thave any errors in the expression evaluation
                    // we might have errors in the formatting, if present
                    _errorManager.LogStringFormatError(formatErrorObject);
                    if (_errorManager.DisplayErrorStrings)
                    {
                        retVal = _errorManager.FormatErrorString;
                    }
                }
            }
            return retVal;
        }

        protected bool EvaluateDisplayCondition(PSObject so, ExpressionToken conditionToken)
        {
            if (conditionToken == null)
                return true;

            PSPropertyExpression ex = expressionFactory.CreateFromExpressionToken(conditionToken, dataBaseInfo.view.loadingInfo);
            bool retVal = DisplayCondition.Evaluate(so, ex, out PSPropertyExpressionResult expressionResult);

            if (expressionResult?.Exception != null)
            {
                _errorManager.LogPSPropertyExpressionFailedResult(expressionResult, so);
            }
            return retVal;
        }

        internal FormatErrorManager ErrorManager
        {
            get { return _errorManager; }
        }

        private FormatErrorManager _errorManager;

        #region helpers

        protected FormatPropertyField GenerateFormatPropertyField(List<FormatToken> formatTokenList, PSObject so, int enumerationLimit)
        {
            PSPropertyExpressionResult result;
            return GenerateFormatPropertyField(formatTokenList, so, enumerationLimit, out result);
        }

        protected FormatPropertyField GenerateFormatPropertyField(List<FormatToken> formatTokenList, PSObject so, int enumerationLimit, out PSPropertyExpressionResult result)
        {
            result = null;
            FormatPropertyField fpf = new FormatPropertyField();
            if (formatTokenList.Count != 0)
            {
                FormatToken token = formatTokenList[0];
                if (token is FieldPropertyToken fpt)
                {
                    PSPropertyExpression ex = expressionFactory.CreateFromExpressionToken(fpt.expression, dataBaseInfo.view.loadingInfo);
                    fpf.propertyValue = GetExpressionDisplayValue(so, enumerationLimit, ex, fpt.fieldFormattingDirective, out result);
                }
                else if (token is TextToken tt)
                {
                    fpf.propertyValue = dataBaseInfo.db.displayResourceManagerCache.GetTextTokenString(tt);
                }
            }
            else
            {
                fpf.propertyValue = string.Empty;
            }
            return fpf;
        }

        #endregion
    }
}
