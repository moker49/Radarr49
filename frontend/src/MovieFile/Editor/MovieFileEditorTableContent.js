import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { kinds, sortDirections } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import MovieFileEditorRow from './MovieFileEditorRow';
import styles from './MovieFileEditorTableContent.css';

class MovieFileEditorTableContent extends Component {

  //
  // Render

  render() {
    const {
      items,
      error,
      columns,
      sortKey,
      sortDirection,
      onSortPress,
      onTableOptionChange
    } = this.props;

    return (
      <div>
        {
          error ?
            <Alert kind={kinds.DANGER}>
              {getErrorMessage(error, translate('FailedToDeleteMovieFile'))}
            </Alert> :
            null
        }
        {
          !items.length &&
            <div className={styles.blankpad}>
              {translate('NoMovieFilesToManage')}
            </div>
        }

        {
          !!items.length &&
            <Table
              columns={columns}
              sortKey={sortKey}
              sortDirection={sortDirection}
              onSortPress={onSortPress}
              onTableOptionChange={onTableOptionChange}
            >
              <TableBody>
                {
                  items.map((item) => {
                    return (
                      <MovieFileEditorRow
                        key={item.id}
                        columns={columns}
                        {...item}
                        onDeletePress={this.props.onDeletePress}
                      />
                    );
                  })
                }
              </TableBody>
            </Table>
        }

      </div>
    );
  }
}

MovieFileEditorTableContent.propTypes = {
  movieId: PropTypes.number,
  error: PropTypes.object,
  isDeleting: PropTypes.bool.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  sortKey: PropTypes.string.isRequired,
  sortDirection: PropTypes.oneOf(sortDirections.all),
  onTableOptionChange: PropTypes.func.isRequired,
  onSortPress: PropTypes.func.isRequired,
  onDeletePress: PropTypes.func.isRequired
};

export default MovieFileEditorTableContent;
